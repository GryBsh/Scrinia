"""Deterministic bounded batches using the pinned Qwen tokenizer and image processor."""
import copy
import hashlib
import json
from filesystem import Path, io_path, canonical_path, canonical_paths
from trajectory_input import canonical, digest, PROCESSOR_REVISION

ALLOWED_TOOLS = {"remember", "recall", "search", "list"}
INSTRUCTIONS = canonical_path(io_path(Path(__file__)).resolve()).parents[1] / "Scrinia.LongMemEvalV2.Worker/Learning/learning-instructions.txt"

def instruction_text():
    return io_path(INSTRUCTIONS).read_text(encoding="utf-8").strip()

def catalog_hash(tools):
    # Match ToolCatalog's default Utf8JsonWriter escaping, including HTML-sensitive ASCII.
    def quote(text):
        value = json.dumps(text, ensure_ascii=True)
        value = value.replace('\\"', '\\u0022')
        for character in [chr(n) for n in (39,43,60,62,38,96)]:
            value = value.replace(character, "\\u" + format(ord(character), "04X"))
        import re
        return re.sub(r"\\u([0-9a-f]{4})", lambda m: "\\u"+m.group(1).upper(), value)
    def encode(value):
        if isinstance(value,dict):
            return "{"+",".join(quote(k)+":"+encode(v) for k,v in sorted(value.items()))+"}"
        if isinstance(value,list): return "["+",".join(encode(v) for v in value)+"]"
        if isinstance(value,str): return quote(value)
        return json.dumps(value,separators=(",",":"))
    return digest(encode(sorted(tools,key=lambda t:t["name"])).encode("utf-8"))

def function_tools(tools):
    return [{"type":"function","function":{"name":t["name"],"description":t.get("description",""),
        "parameters":t["inputSchema"]}} for t in sorted(tools,key=lambda t:t["name"]) if t["name"] in ALLOWED_TOOLS]

class ProcessorCounter:
    revision = PROCESSOR_REVISION
    def __init__(self, assets):
        from transformers import AutoProcessor
        self.assets = Path(assets)
        lock = json.loads(io_path(Path(__file__).with_name("upstream.lock.json")).read_text(encoding="utf-8"))
        if lock["modelProcessorRevision"] != self.revision: raise ValueError("Processor revision is not pinned.")
        for name, expected in lock["processorFiles"].items():
            if digest((io_path(self.assets/name)).read_bytes()) != expected: raise ValueError("Processor assets changed.")
        self.processor = AutoProcessor.from_pretrained(str(self.assets), local_files_only=True)
    def text_tokens(self,text):
        return len(self.processor.tokenizer.encode(text,add_special_tokens=False))
    def count(self,text,images,tools):
        from PIL import Image
        content=[{"type":"text","text":text}]+[{"type":"image"} for _ in images]
        messages=[{"role":"system","content":instruction_text()},{"role":"user","content":content}]
        serialized=self.processor.apply_chat_template(messages,tools=function_tools(tools),
            tokenize=False,add_generation_prompt=True,enable_thinking=True)
        opened=[]
        try:
            for image in images:
                if digest(io_path(Path(image["path"])).read_bytes()) != image["sha256"]: raise ValueError("Image changed.")
                with Image.open(io_path(image["path"])) as source: opened.append(source.convert("RGB"))
            result=self.processor(text=[serialized],images=opened or None,return_tensors="pt")
            return int(result["input_ids"].shape[-1])
        finally:
            for image in opened:image.close()

class BatchPlanner:
    def __init__(self,counter,tools,*,input_tokens=24576,output_tokens=16384,
                 image_limit=4,fragment_tokens=4096,served_context=65536):
        if (input_tokens<1 or output_tokens<1 or image_limit<1 or fragment_tokens<1
            or input_tokens>24576 or output_tokens>16384 or image_limit>4 or fragment_tokens>4096
            or served_context<input_tokens+output_tokens):
            raise ValueError("Invalid learning/context limits.")
        self.counter=counter;self.tools=tools;self.input_tokens=input_tokens
        self.output_tokens=output_tokens;self.image_limit=image_limit
        self.fragment_tokens=fragment_tokens;self.served_context=served_context
    def _fragments(self,trajectory):
        result=[]
        for state in trajectory["states"]:
            text=state["text"]; start=0; byte_offset=0; fragment=0
            while start<len(text) or (not text and fragment==0):
                lo=start+1;hi=len(text);end=start
                while lo<=hi:
                    mid=(lo+hi)//2
                    if self.counter.text_tokens(text[start:mid])<=self.fragment_tokens:
                        end=mid;lo=mid+1
                    else:hi=mid-1
                if text and end==start: raise ValueError("One character exceeds the fragment cap.")
                part=text[start:end]; length=len(part.encode("utf-8"))
                source_id=digest((trajectory["sourceHash"]+":"+str(state["stateIndex"])+":"+str(fragment)).encode())
                result.append({"sourceId":source_id,"stateIndex":state["stateIndex"],"fragmentIndex":fragment,
                    "startByte":byte_offset,"endByte":byte_offset+length,"text":part,"textHash":digest(part.encode("utf-8"))})
                start=end;byte_offset+=length;fragment+=1
                if not text:break
        return result
    def prepare(self,trajectory):
        result=copy.deepcopy(trajectory);fragments=self._fragments(trajectory);batches=[];pending=[]
        def make(items):
            states={s["stateIndex"]:s for s in trajectory["states"]};images=[];seen=set();records=[]
            for item in items:
                state=states[item["stateIndex"]]
                records.append({**item,"step":state["step"],"url":state["url"],"action":state["action"],"thoughts":state["thoughts"],"screenshotSha256":state["screenshot"]["sha256"]})
                image=state["screenshot"]
                if image["sha256"] not in seen:images.append(image);seen.add(image["sha256"])
            message=canonical({"trajectoryId":trajectory["id"],"trajectorySourceHash":trajectory["sourceHash"],
                "goal":trajectory["goal"],"outcome":trajectory["outcome"],"startUrl":trajectory["startUrl"],"fragments":records})
            tokens=self.counter.count(message,images,self.tools)
            return {"sourceIds":[f["sourceId"] for f in items],"messageText":message,"images":copy.deepcopy(images),"inputTokens":tokens}
        for fragment in fragments:
            trial=make(pending+[fragment])
            if len(trial["images"])>self.image_limit or trial["inputTokens"]>self.input_tokens:
                if not pending: raise ValueError("Source image/fragment cannot fit by itself.")
                batches.append(make(pending));pending=[fragment]
                single=make(pending)
                if len(single["images"])>self.image_limit or single["inputTokens"]>self.input_tokens:
                    raise ValueError("Source image/fragment cannot fit by itself.")
            else:pending.append(fragment)
        if pending:batches.append(make(pending))
        result["fragments"]=fragments;result["batches"]=batches
        return result
