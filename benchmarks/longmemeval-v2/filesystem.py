"""Explicit filesystem names; protocol and containment paths remain ordinary."""
import os
from pathlib import Path

_PREFIX = chr(92) * 2 + "?" + chr(92)

def canonical_path(value):
    value = str(value)
    if os.name == "nt" and value.startswith(_PREFIX):
        tail = value[len(_PREFIX):]
        value = chr(92) * 2 + tail[4:] if tail.startswith("UNC" + chr(92)) else tail
    return Path(value)

def io_path(value):
    path = canonical_path(value)
    name = str(path)
    if os.name != "nt" or not path.is_absolute():
        return path
    if name.startswith(chr(92) * 2):
        return Path(_PREFIX + "UNC" + chr(92) + name[2:])
    return Path(_PREFIX + name)

def io_name(value):
    return str(io_path(value))

def canonical_paths(values):
    return (canonical_path(value) for value in values)
