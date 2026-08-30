# Task 10 scope preservation report

## Change

Added an MCP regression covering `remember('/topic/item')`. The write now uses the resolved `(scope, subject)` pair when constructing `MemoryWrite` for remember, append, and compact. The typed construction preserves the ephemeral address form as well.

## Verification

- Regression: passed.
- Focused MCP/address/conformance tests: 79 passed.
- Ephemeral remember/append coverage: 4 passed.
- Solution build: passed with 0 errors.
- Maintenance event-sink suite: 4 passed, 18 failed on pre-existing direct legacy setup assumptions documented by the debug report; no server files or Task 11 files were changed.
