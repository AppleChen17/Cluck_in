# Local LLM findings

Measured on 2026-09-19 against real Gmail and Slack messages fetched by
`src/external`, using `scripts/try_llm.py`.

This answers the risk `docs/PRODUCT_SPEC_MVP.md` §12 calls "整個 MVP 最核心功能的
成敗點": can a local model classify **our** messages, which are in Chinese?

**Machine:** Intel Core Ultra 7 155H (16 cores) + Intel Arc integrated graphics,
Windows 11, Ollama 0.34.2. No discrete GPU.

---

## Summary

1. **`gemma3:1b` cannot do this job.** It reads Chinese fine, but it cannot map
   its own understanding onto the decision label. Use `qwen2.5:3b-instruct`.
2. **Two configuration changes cut latency by more than half**, and neither
   costs any quality: use `127.0.0.1` rather than `localhost`, and cap message
   content at ~400 characters.
3. **The integrated GPU is not worth chasing.** It can be enabled, but it buys
   7% on the 3B model and nothing on the 1B.

---

## 1. Model quality

Four hand-written cases, `currentTask = "Finish Cluck In prototype"`:

| Message | gemma3:1b | qwen2.5:3b-instruct |
|---|---|---|
| `Cluck In 的 build 掛了，prototype 現在跑不起來` | `hold` rel=1.00 urg=0.00 | `urgent` rel=0.90 urg=0.90 |
| `demo 改到下午兩點，請一點前更新投影片` | `hold` rel=1.00 urg=0.00 | `hold` rel=0.80 urg=0.10 |
| `good morning` | `hold` rel=1.00 urg=0.00 | `allow` rel=0.10 urg=0.00 |
| `中午要不要一起吃飯？` | `hold` rel=0.00 urg=0.00 | `allow` rel=0.10 urg=0.00 |

Scoring these against hand-written labels is weak evidence — n=4, the labels are
one person's judgement, and `docs/data-contracts.md` says the urgent/hold
threshold is deliberately undefined. Two stronger, label-independent findings:

**`gemma3:1b` has zero output variance.** Across 10 classifications (6 real
messages plus these 4), it returned `hold` every single time. Whatever the
correct answers are, a classifier with a constant output carries no information.

**Its numbers contradict its own text.**

```
good morning        → reason: "doesn't relate to the current task"   relevance: 1.00
中午要不要一起吃飯？  → reason: "unrelated to the current task"        relevance: 0.00
```

Two equally irrelevant messages, scored 1.00 and 0.00. And on the build failure
it wrote "directly impacts the current task" while returning `hold` with
`urgency: 0.00`.

This matters more than the label accuracy because `docs/data-contracts.md`
states consumers "branch on `decision`, not natural-language parsing" — so the
one field the app is allowed to read is the one that is wrong. A correct
`reason` is no help to anybody.

Constrained decoding (Ollama's `format` parameter) guarantees the JSON is
structurally valid and the enum members are real. It cannot supply judgement.

`qwen2.5:3b-instruct` separates relevant from irrelevant (0.90/0.80 versus
0.10/0.10), which is what lets the app set its own threshold, as the contract
intends.

**Caveat:** its `reason` comes back in Simplified Chinese ("消息", "邮件") and
occasionally in English, despite the prompt asking for the message's own
language. For a Traditional Chinese demo that will look wrong on the dashboard.
Worth a prompt tweak, or post-processing.

---

## 2. Latency

### The `localhost` trap — 2.05 seconds per request, for free

`src/ai-engine/config.py` currently reads:

```python
OLLAMA_BASE_URL = "http://localhost:11434"
```

Ollama binds IPv4 only (`OLLAMA_HOST:http://127.0.0.1:11434`). On Windows
`localhost` resolves to `::1` first; that connection fails and the IPv4 retry
lands about two seconds later. Identical requests, measured with n=5:

| Model | via `localhost` | via `127.0.0.1` |
|---|---|---|
| gemma3:1b | 4.24s | **2.81s** |
| qwen2.5:3b-instruct | 4.65s | **3.12s** |

The overhead is a flat 2.05s and is invisible in Ollama's own timing, which
reported 2.19s and 2.59s for those same calls. **Change one word, save two
seconds.**

### Content length is the other lever

A real 999-character Notion newsletter, `qwen2.5:3b-instruct`:

| `max_chars` | latency | prompt tokens | verdict |
|---|---|---|---|
| 1200 | 6.08s | 599 | `allow` rel=0.1 |
| 800 | 6.32s | 549 | `allow` rel=0.1 |
| 500 | 4.70s | 420 | `allow` rel=0.1 |
| **300** | **3.21s** | 284 | `allow` rel=0.1 |
| 150 | 2.91s | 212 | `allow` rel=0.1 |

The verdict is unchanged all the way down. Prompt evaluation dominates on long
mail, and the subject plus the opening lines carry the signal. §8.3 of the spec
already names this as a valid lever; the data says it is the strongest one.

### Where the irreducible 2-3 seconds goes

Ollama reports its own breakdown, and it is not model loading:

```
qwen2.5:3b-instruct          gemma3:1b
  load          0.01s          load          0.01s
  prompt eval   0.10s          prompt eval   0.15s   (input is cheap)
  generation    2.37s          generation    1.90s   <- 95% of the time
  total         2.49s          total         2.07s
```

Generation is autoregressive: one token at a time, and every token requires
reading the whole model's weights from memory. It cannot be parallelized, so

```
time = output tokens / tokens per second
qwen:  45 / 19.0 = 2.37s      gemma: 66 / 34.7 = 1.90s
```

Tokens per second is bounded by **memory bandwidth**, not arithmetic:
qwen2.5:3b is ~2.0 GB at 19 tok/s (~38 GB/s), gemma3:1b ~0.8 GB at 34.7 tok/s
(~28 GB/s).

That explains two things at once. **The integrated GPU cannot help**, because it
shares the same memory bus — the bottleneck is moving weights, not computing on
them. And **a smaller model barely helps**, because gemma3:1b is 1.83x faster per
token but emits 47% more tokens, which nearly cancels out.

So the only real lever left is emitting fewer tokens. Which turns out to be a
trap — see the next section.

### Do not shorten or reorder the output

The obvious saving is the `reason` string, the longest part of the output.
It cannot be taken. Six cases, `qwen2.5:3b-instruct`:

| Schema field order | 該打斷 (3) | 不該打斷 (3) | relevance spread | median |
|---|---|---|---|---|
| **`reason` first** (current) | **2/3** | **3/3** | **0.9/0.8/0.8 vs 0.1/0.1/0.1** | 4.35s |
| `reason` last | 0/3 | 2/3 | 0.8/0.2/0.2 vs 0.2/0.8/0.2 | 4.73s |
| no `reason` at all | 0/3 | 1/3 | 1.0/0.8/0.8 vs 0.2/0.8/0.2 | 3.02s |

Under a grammar constraint, **field order in `properties` is generation order**.
Putting `reason` first makes the model write its justification before it commits
to a label — miniature chain-of-thought. Move it last and the judgement collapses
into exactly gemma3:1b's failure mode: relevance stops discriminating.

Dropping it entirely saves 1.3s and destroys the answer. Moving it last saves
nothing at all and still destroys the answer. Asking for a terse reason
("at most 12 characters") saves 0.15s and returns useless text like `Cluck In`.

**Keep `reason` first.** It is not overhead; it is what makes the classification
work.

### What does *not* help

**`num_predict`** — sweeping 200 → 120 → 80 → 50 changed nothing (4.63s to
4.69s), because the model emits only 40-60 tokens and never reaches the cap. At
30 it does get faster (3.79s) by truncating the JSON mid-string, which then
fails to parse. Leave it at 200.

**A smaller model** — `gemma3:1b` (1B) runs at 2.81s and `qwen2.5:3b` (3B) at
3.12s. Three times the parameters costs 11% more time. Model size is not the
bottleneck, so trading quality for size buys almost nothing.

---

## 3. The integrated GPU

Stock Ollama reports `100% CPU` because it disables integrated GPUs by default.
It can be switched on — the Vulkan backend ships with Ollama
(`lib/ollama/vulkan/ggml-vulkan.dll`), `vulkan-1.dll` is in System32, and the
Arc driver is current:

```powershell
$env:OLLAMA_IGPU_ENABLE='1'
ollama serve
```

Ollama then reports:

```
inference compute  library=Vulkan  description="Intel(R) Arc(TM) Graphics"
                   type=iGPU  total="17.9 GiB"
qwen2.5:3b-instruct   2.2 GB   100% GPU
```

It works. It is just not worth it (n=5 each):

| Model | CPU | Arc iGPU |
|---|---|---|
| gemma3:1b | 4.16s | 4.23s |
| qwen2.5:3b-instruct | 4.65s | 4.31s |

7% for the 3B model, slightly negative for the 1B. An integrated GPU shares
memory bandwidth with the CPU, and against 16 CPU cores it is roughly a wash for
models this small. Not worth the configuration risk before a demo.

*(Note: `ollama` is not on PATH until a new terminal is opened after install;
the binary is at `%LOCALAPPDATA%\Programs\Ollama\ollama.exe`.)*

---

## Recommended settings

```ini
OLLAMA_BASE_URL=http://127.0.0.1:11434   # NOT localhost - costs 2.05s
OLLAMA_MODEL=qwen2.5:3b-instruct         # NOT gemma3:1b - see section 1
OLLAMA_KEEP_ALIVE=30m                    # default is 5m; an idle demo goes cold
AI_MAX_CONTENT_CHARS=400
AI_NUM_CTX=2048                          # raise together with max content chars
AI_NUM_PREDICT=200                       # lower does not help and breaks JSON
AI_TEMPERATURE=0
```

### What to actually expect

Measured over 18 classifications of the 6 real messages, external service
stopped so nothing else was competing for the CPU:

```
p50 = 3.89s    p95 = 7.35s    min = 2.99s    max = 8.57s
```

**Variance is the problem, not the median.** Two requests with nearly identical
token counts (333 and 369 prompt tokens, 50 and 48 generated) took 8.62s and
3.85s. Stopping every other process barely narrowed the spread, so this is the
laptop's own frequency scaling, not contention with `src/external`.

Note also that the first call for a given message is consistently the slowest;
repeats are faster because Ollama caches the prompt prefix. Real traffic is all
first calls, so plan against those.

**A 2 second timeout is not achievable on this hardware, and 3 seconds is met
only about half the time.** §3 success criterion 2 of the spec gives the whole
Pat flow 3 seconds. Options, in order of preference:

1. **Classify in the background and show results as they land.** Pat shows
   "小雞正在看…" immediately and fills in when the answer arrives. This removes
   the deadline instead of fighting it, and the `thinking` chicken mood the spec
   already adds in §10.1 is exactly the affordance for it.
2. **Raise the timeout to 8 seconds** and accept that Pat sometimes takes a
   while.
3. **Keep a 3 second timeout and fail open**, accepting that roughly half of
   all classifications return the fallback. Not recommended: a feature that
   silently degrades half the time is worse than one that visibly takes longer.

### Two things to design around

**Ollama serialises requests per model.** `OLLAMA_NUM_PARALLEL` defaults to 1,
so ten messages cost ten times the latency, not one. Either set
`OLLAMA_NUM_PARALLEL=2` before starting the server, or cap the Pat batch at
three to five messages.

**Cold start.** The first call after the model is evicted takes far longer
(24.4s observed when switching models). `keep_alive: "30m"` on every request
plus a warm-up call at startup are both necessary.

## Reproducing

```powershell
.\.venv\Scripts\python.exe src\external\scripts\try_llm.py --fixtures
.\.venv\Scripts\python.exe src\external\scripts\try_llm.py --model gemma3:1b
.\.venv\Scripts\python.exe src\external\scripts\try_llm.py --model qwen2.5:3b-instruct --repeat 3
```

Ctrl+C stops a run and still prints the statistics gathered so far.
