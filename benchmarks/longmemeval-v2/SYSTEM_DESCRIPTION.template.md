# Scrinia LongMemEval V2 system description

Draft until live evidence and operator details are recorded. This file does not attest deployment capabilities.

Controller and reader: unsloth/Qwen3.5-9B-MTP-GGUF:BF16, BF16 with MTP requested. Judge: GPT-5.2, medium reasoning. Hardware, exact model build/revision, deployed context capacity, image support and reader thinking default need live verification and deployment evidence.

The frozen source archive contains complete learning/retrieval prompts and relevant dirty and untracked source bytes. The manifest binds runtime payloads, Python dependencies, canonical MCP tools/guide, processor assets, both domain datasets and original image policy. Inputs and questions remain isolated; queries do not train memory. No V1 grades are reused.

Controller output 16,384 tokens; reader output 20,000 and memory context 200,000; judge output 4,096. Prompt-build concurrency and reader-request concurrency are both one. Controller sampling 0.6/0.95/20 with thinking enabled; timeout 180 seconds, episode 300 seconds, 40 model requests, five shared attempts, transport backoff 5/10/20/40 seconds and one response recovery. Prompts, binary and policy changes require a new experiment.

Recovery uses verified committed generation receipts, refuses uncertain writes and isolates query copies. Process crash/cancellation tests do not establish host or power-loss durability. The official reader request remains unchanged and relies on the deployed server thinking default.

No real V2 examples were used in development fixtures. Record accidental exposures in the frozen exposure log. Source code/data revisions, hardware/deployment evidence and unresolved prerequisites appear in the appended frozen configuration. A source ZIP is passed as the official utility's single code file; organizer acceptance of a multi-file .NET source archive remains unresolved.

The estimates include unsuccessful attempts. Unknown usage/cost is null; fake-provider timings establish no real-model performance or cost.
