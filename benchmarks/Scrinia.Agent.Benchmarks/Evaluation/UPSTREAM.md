# LongMemEval grading prompts

The five templates in `rubrics.json` are copied from
[LongMemEval's evaluator](https://github.com/xiaowu0162/LongMemEval/blob/d6dc8b50a2d9ac0c99485ea28fa5755c62414c34/src/evaluation/evaluate_qa.py),
revision `d6dc8b50a2d9ac0c99485ea28fa5755c62414c34`.
Each template includes a SHA-256 of its exact UTF-8 text. The runner also hashes the
complete embedded resource, including the revision, for run identity.

The checked-in golden fixture was generated independently by the upstream pure
`get_anscheck_prompt` function, for all six categories with and without abstention.
Tests compare complete prompt strings. Python is not required to run the tests or benchmark.

Verdict parsing intentionally requires an exact yes/no reply after trimming and
ignoring case. The upstream evaluator uses substring matching. Malformed replies
remain grading failures here. Model, sampling parameters, and usage are recorded
from the actual configured judge rather than being labeled as the upstream default.

## Upstream license

MIT License

Copyright (c) 2024 Di Wu

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR
IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
