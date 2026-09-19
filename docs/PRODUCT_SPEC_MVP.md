# Cluck In — MVP Product Spec

> 版本：v0.3 (MVP)｜日期：2026-09-19｜狀態：Draft
> 對齊 commit `d7af827`（shared schemas + ai-engine + web skeleton）
> 硬體：Logitech MX Creative Console（9 鍵 Keypad + Dialpad）

---

## 1. 產品一句話

**Cluck In 是一隻住在 Creative Console 上的電子雞，幫你守住一段專注時間：由 AI 讀懂你打開的網頁和收到的訊息在講什麼，跟現在的任務無關就默默擋掉；想知道外面發生什麼事時拍牠一下，牠用一句話告訴你。**

---

## 2. 目前 repo 狀態

| 模組 | 技術 | 現況 |
|---|---|---|
| `src/app/` | C# | 只有一行 `Console.WriteLine` |
| `src/logitech/` | C# | 空 class library |
| `src/external/` | C# | 空 class library |
| `src/actions/` | C# | 空 class library |
| `src/ai-engine/` | Python / FastAPI | **有 skeleton**：`/analyze-message`、`/draft-reply`，`LLMProvider` 抽象 + `MockProvider`，config 指向 Ollama `gemma3:1b` |
| `src/web/` | React + Vite | **有 skeleton**：靜態 dashboard（mode / task / timer / 訊息 / AI 決策分數 / 小雞 / 活動紀錄） |
| `shared/schemas/` | JSON Schema | 8 份契約 + fixtures，已定義 InputEvent、ExternalMessage、SessionContext、AIRequest、AIDecision、ActionCommand、AppState |

**已經對的方向**：模組切分、`AppState` 快照推給 web / logitech、AI 用 provider 抽象、訊息分類的契約鏈。本 spec 沿用這些，不推翻。

**現有契約缺的東西**：整套契約是**訊息導向**的（ExternalMessage → AIDecision），但 MVP 的另一半核心是**網頁導向**的判斷，目前沒有任何對應契約。§8 列出需要補的部分。

---

## 3. MVP 範圍

### In scope

| # | 功能 | 主要模組 |
|---|---|---|
| 1 | **AI 網頁守門員** — Focus 期間 AI 讀網頁內容，判斷與當前任務是否相關，無關就擋 | browser + ai-engine + app + actions |
| 2 | **Pat 訊息插播** — Focus 中按鍵，AI 一句話說「這段時間有沒有事」（Gmail） | external + ai-engine + app |
| 3 | **番茄鐘** — 預設 45 分鐘，Dialpad 啟動，7/8/9 顯示倒數 | logitech + app |
| 4 | **Task 切換** — 4 號鍵循環切換，白名單跟著換 | logitech + app |
| 5 | **電子雞 + 飼料** — 5 號鍵動畫、6 號鍵鳥巢 | logitech + app |
| 6 | **Web Dashboard** — 所有文字資訊的顯示面 | web |

### Out of scope

Auto Mode、Automation 自動回覆（`/draft-reply` 先擱著）、Slack、Google Calendar、應用程式層級阻擋（**只管瀏覽器**）、工作節奏分析、電子雞養成（成長／進化／飢餓衰減）、聲音回饋、桌面原生視窗（用 web dashboard 取代）。

> `src/actions/` 在 MVP 仍有工作：執行 `SHOW_NOTIFICATION` 與新增的 `BLOCK_PAGE`。不會沒事做。

### 成功判準（Demo 可驗證）

1. 選任務 → 進 Focus → 打開內容與任務無關的網頁，AI 讀完內容後蓋上攔截 overlay；打開內容相關的網頁（**即使不在白名單上**）被放行並自動記住。重點是**依內容判斷，不是網域黑名單** — 同一個網域可能放行也可能攔截。
2. Focus 中按 Pat → 3 秒內 dashboard 出現一句話摘要，且**計時器不中斷**。
3. 番茄鐘跑完 → 回到 Idle → 小雞開心 → 鳥巢飼料 +1。

---

## 4. 為什麼需要 Chrome Extension

功能 1 需要三件事，而**只有瀏覽器擴充能做到**：

| 需要 | C# app 能做嗎 | Extension |
|---|---|---|
| 知道現在在看哪個網址 | ✗ 只拿得到 Chrome 視窗標題 | ✓ |
| 讀到頁面在講什麼 | ✗ 沒辦法 | ✓ 讀 DOM |
| 把使用者擋下來 | ✗ 頂多最小化整個視窗 | ✓ |

替代方案（UI Automation 讀標題、Chrome remote debugging port）都是只拿得到標題、擋不住、或更脆弱。

**結論：功能 1 留著，extension 就是必需品；功能 1 砍掉，extension 一起砍。** 建議留 — 沒有它，MVP 只剩「番茄鐘 + Gmail 摘要 + 小雞」。

**代價：現在 5 個模組 5 個人，extension 是第 6 個模組，沒有主人。** 建議給 `src/web` 的人（同樣 TypeScript），並把 extension 壓到**沒有任何自己的 UI**（估約 100 行）：

- content script：頁面載入後抽 `title` + `<meta description>` + 可見文字前 1000 字，送給 app
- 收到 `block` verdict：在**原頁面上蓋一層 overlay**（不導向新頁面、不用 `tabs.update`、不用多一個 HTML 檔）
- 所有文字資訊（Pat 摘要、攔截紀錄、倒數）都在既有的 React dashboard，extension 不做

---

## 5. 模組對應與分工

```
┌──────────────┐  InputEvent   ┌──────────────────┐  AppState   ┌──────────────┐
│ src/logitech │ ─────────────▶│    src/app       │ ───────────▶│   src/web    │
│ 9 鍵 + Dial  │◀───────────── │ 狀態機 / 計時器   │             │  Dashboard   │
│ 鍵面圖像      │   AppState    │ 任務 + 白名單     │             └──────────────┘
└──────────────┘               │ 契約編排          │
                               └───┬──────────┬───┘
┌──────────────┐ ExternalMessage   │          │  ActionCommand   ┌──────────────┐
│ src/external │ ──────────────────┘          └─────────────────▶│ src/actions  │
│ Gmail        │                                                  │ 執行命令      │
└──────────────┘                                                  └──────┬───────┘
┌──────────────┐  PageVisit ↑                    AIRequest │            │ BLOCK_PAGE
│ src/browser  │ ───────────┘                  AnalyzePage ▼            │
│ (新) Chrome  │◀──────────────────────────  ┌──────────────────┐       │
│  Extension   │        PageVerdict           │  src/ai-engine   │       │
└──────────────┘◀─────────────────────────────┴──────────────────┘◀──────┘
```

| 模組 | MVP 要做的事 |
|---|---|
| `src/app` | 狀態機（idle/focus）、計時器、任務與白名單持久化（本機 JSON）、決定何時呼叫 AI、產生 `AppState` |
| `src/logitech` | 9 鍵 + Dialpad 事件 → `InputEvent`；訂閱 `AppState` → 推送鍵面圖像（含小雞動畫、7/8/9 數字） |
| `src/external` | Gmail read-only OAuth、輪詢新信 → `ExternalMessage` |
| `src/ai-engine` | 既有 `/analyze-message` 接真實 LLM；**新增 `/analyze-page`** |
| `src/actions` | 執行 `ActionCommand`：`SHOW_NOTIFICATION`、**新增 `BLOCK_PAGE`** |
| `src/web` | 把 App.tsx 從 mock 接上真實 `AppState`；新增 Pat 摘要與攔截紀錄卡片 |
| `src/browser`（新） | content script 抽內容、overlay 攔截、WebSocket 接 app |

---

## 6. 狀態機

只有兩個狀態（`AppState.mode` 的 `auto` 在 MVP 不使用，但保留在 enum 裡）。

```
   ┌─────────┐   Dialpad 鈕：設定時長 → 開始    ┌──────────┐
   │  IDLE   │ ───────────────────────────────▶ │  FOCUS   │
   │         │ ◀─────────────────────────────── │          │
   └─────────┘   倒數歸零 ／ 1 號鍵長按提前結束  └──────────┘
        │                                            │  ▲
        │ 餵食、看上次摘要、正常上網                   └──┘
        ▼                                     2 Pat（不離開 FOCUS）
```

| | IDLE | FOCUS |
|---|---|---|
| 小雞 mood | `idle` | `focused` |
| 網頁 | **完全不管**，extension 不送 PageVisit | AI 守門員開啟 |
| 訊息 | 正常 | 靜音收集（`heldMessages` 累加），不推播 |
| 7/8/9 | 現在時間（24 小時制） | 倒數剩餘時間 |

Session 結束直接回 IDLE，上一次的完整摘要留在 dashboard。**不另設 Reward 狀態。**

---

## 7. Creative Console 介面

```
┌──────────────┬──────────────┬──────────────┐
│ 1  MODE      │ 2  PAT       │ 3  (保留)     │
├──────────────┼──────────────┼──────────────┤
│ 4  TASK      │ 5  CHICKEN   │ 6  NEST       │
├──────────────┼──────────────┼──────────────┤
│ 7  HH        │ 8  MM        │ 9  SS         │
└──────────────┴──────────────┴──────────────┘
```

兩個模式版面不變，只有鍵面內容與行為改變（MVP 不做動態換版）。

| # | IDLE | FOCUS | 對應 InputEvent |
|---|---|---|---|
| 1 | 顯示 `IDLE`（灰） | 顯示 `FOCUS`（亮）；長按 1.5s 提前結束，不給飼料 | `STOP_FOCUS` |
| 2 | 顯示上次 session 摘要 | Pat：AI 摘要這段時間的信 | `PET_CHICKEN` |
| 3 | 暗鍵 | 暗鍵 | — |
| 4 | 循環切換任務，鍵面顯示任務名 | 同左，白名單即時跟著換 | `SELECT_TASK` |
| 5 | idle 動畫 | focused 動畫 | — |
| 6 | 巢 + 飼料數；單擊餵食（−1） | 巢 + 飼料數（按下無作用） | `FEED_CHICKEN` |
| 7/8/9 | 現在時間 | 倒數 | — |

**Dialpad**

| 操作 | IDLE | FOCUS |
|---|---|---|
| 按鈕 | 按一下進設定 → 轉 Dial 調時長 → 再按一下開始（`START_FOCUS`，payload 帶 `focusDurationSeconds`） | 無作用，避免誤觸中斷 |
| 旋轉 | 調時長，5 分鐘為 step，5–120 分鐘，**預設 45 分鐘**，即時反映在 7/8/9 | 無作用 |

任務清單（名稱、描述、種子白名單）在 **web dashboard 的設定頁**維護，不做額外視窗。

---

## 8. AI 功能

### 8.1 網頁守門員（核心，需要新契約）

**判斷時機：頁面載入後由 content script 讀 DOM，不是導航前攔截。**

刻意的取捨：導航前只拿得到 URL，AI 判斷不了「這篇在講什麼」。改成載入後判斷，AI 拿得到真正的內容，實作也簡單得多。代價是頁面會先開出來、約 1 秒後才被蓋住 — 對 MVP 可接受，而且「小雞把你抓回來」在 demo 上是有畫面的。

```
頁面載入完成 (content script)
    │
    ├─ 不在 FOCUS？               → 什麼都不做
    ├─ domain ∈ task.allowlist？  → 放行（0 延遲、不打 AI）
    ├─ domain ∈ task.blocklist？  → 直接蓋 overlay（0 延遲、不打 AI）
    └─ 未知 → app 呼叫 /analyze-page（5 號鍵切 thinking）
            ├─ allow → 放行 + 寫入 allowlist
            └─ block → BLOCK_PAGE + 寫入 blocklist
```

**新增 ai-engine endpoint**（沿用現有 `schemas.py` 風格）

```python
class AnalyzePageRequest(BaseModel):
    current_task: str
    task_description: str | None = None
    url: str
    title: str
    content: str                       # 可見文字前 1000 字

class AnalyzePageResponse(BaseModel):
    decision: Literal["allow", "block"]
    relevant: bool
    confidence: float                  # 0–1
    reason: str                        # ≤ 30 字，顯示在 overlay 上
```

**新增 app ↔ browser 的兩個 WebSocket 訊息**（localhost，不需要進 shared/schemas 的完整契約鏈）

```jsonc
// browser → app
{ "type": "PAGE_VISIT", "tabId": 42, "url": "...", "title": "...", "content": "..." }
// app → browser
{ "type": "PAGE_VERDICT", "tabId": 42, "decision": "block", "reason": "這是遊戲實況影片" }
```

**新增 ActionCommand type：`BLOCK_PAGE`**，payload `{ tabId, reason }`，由 `src/actions` 執行（送出 PAGE_VERDICT）。

**設計原則**

- **依內容判斷，不是網域。** 同一個網域可能放行也可能攔截 — 看那頁在講什麼。Demo 一定要秀這個對比。
- **誤擋比誤放行糟。** `confidence < 0.6` 一律放行。小雞是提醒，不是防火牆。
- **超時放行。** AI 超過 2 秒沒回就放行，絕不讓使用者卡住。
- **判斷結果會記住**，寫進該任務的 allowlist / blocklist 並持久化，用越久越少打擾、API 呼叫越少。
- **一定有逃生門。**

**攔截 overlay**（content script 注入，不另開頁面）

```
        [ 小雞圖 ]

   這跟「Hackathon Proposal」好像沒關係

   理由：這是一支遊戲實況影片

   [ 回上一頁 ]   [ 我真的需要 ]
```

「我真的需要」= 放行這個網域 5 分鐘，記一次 `override`。這個計數會誠實顯示在 dashboard 的 session 摘要裡，也是日後做工作節奏分析唯一的資料來源。

**種子白名單**（建立任務時一鍵套用，降低第一次使用的 AI 呼叫量）

| 任務類型 | 種子白名單 |
|---|---|
| Coding | github.com, stackoverflow.com, localhost |
| Survey / Research | scholar.google.com, arxiv.org, wikipedia.org |
| Writing / Proposal | docs.google.com, notion.so, claude.ai, figma.com |
| Custom | 空 |

### 8.2 Pat 訊息插播（沿用既有契約鏈）

**觸發**：Focus 中按 2 號鍵（`PET_CHICKEN`）。Idle 按則顯示上一次 session 摘要。

**流程**：`external` 輪詢 Gmail → `ExternalMessage` → app 組 `AIRequest`（message + SessionContext）→ `/analyze-message` → `AIDecision` → app 彙整成一句話 → `SHOW_NOTIFICATION` → dashboard 顯示。

現有 `AnalyzeMessageResponse` 已經夠用（`priority` / `relevant` / `should_interrupt` / `summary`），MVP **不需要改這支 API**。app 端把多則 `summary` 收斂成一句 headline。

**顯示**：web dashboard 的 Pat 卡片（既有 App.tsx 的訊息／決策卡片改一下就能用）+ 5 號鍵播放 happy 動畫。**不中斷計時器、不離開 Focus。**

**訊息來源**：MVP 只做 **Gmail（read-only OAuth）**。Slack 是下一階段第一順位 — `external` 模組內先留好 interface，schema 的 `source` enum 已經含 `slack`，不用改契約。

### 8.3 模型選擇（需要儘早實測）

`config.py` 目前指向 Ollama `gemma3:1b`。這對兩個功能的難度差很多：

- `/analyze-message`：短文本分類，1b 模型**可能夠用**。
- `/analyze-page`：要讀 1000 字內容再判斷相關性，**1b 模型很可能不夠**（品質與延遲都是問題）。

建議：`LLMProvider` 抽象已經在了，**第一天就拿真實網頁實測**，比較 `gemma3:1b`、一個 7–8b 級的本機模型、以及雲端 API 三種的延遲與品質，再決定。內容從 1000 字往下砍也是有效的手段。**不要等到 demo 前一天才發現模型判不動。**

---

## 9. Web Dashboard 的角色

沒有桌面原生視窗，**dashboard 就是唯一的文字顯示面**。現有 App.tsx 的卡片幾乎都用得上，只要從 mock 換成真實 `AppState`：

| 現有卡片 | MVP 改法 |
|---|---|
| Session（mode / task / timer） | 接 `AppState.mode`、`currentTask`、`focusRemainingSeconds`；mode 只留 Idle / Focus |
| Incoming Message | 改成 **Pat 摘要**：一句 headline + 最多 3 則項目 |
| AI Decision（relevance / urgency 分數） | 保留，改接真實 `AIDecision`；demo 時這張卡最能說明「AI 真的有在判斷」 |
| Chicken | 接 `AppState.chicken.mood` |
| Recent Actions | 改成 **本次 session 紀錄**：攔截了哪些網頁（含理由）、override 幾次、held 幾則訊息 |

新增一頁簡單的**任務設定**（名稱、描述、種子白名單）。

傳輸方式（WebSocket 推播 vs 輪詢 `AppState`）由 `app` 與 `web` 兩位自行約定；契約文件已經定義 `AppState` 是快照而非 delta stream，輪詢也可行。

---

## 10. 電子雞動畫

### 10.1 規格（壓到最小）

鍵面動畫 = `logitech` 模組把一連串 PNG 依序推到 5 號鍵。只需要 **4 個狀態、每個 2–3 格，共約 11 張 PNG**：

| mood | 觸發 | 內容 | 格數 |
|---|---|---|---|
| `idle` | IDLE | 站著眨眼／偶爾啄一下 | 2–3，2 fps loop |
| `focused` | FOCUS | 專注表情（戴眼鏡／閉眼） | 2，1 fps loop |
| `thinking` | AI 判斷中 | 頭上一個問號 | 2，4 fps loop |
| `happy` | 完成 session／餵食／Pat 回來 | 跳一下 | 3，一次性 1s |

被 Pat 時不另做動畫（沿用 `thinking` → `happy`）。攔截時也不做鍵面動畫 — overlay 上的靜態小雞圖就夠，原本設想的「張開翅膀擋住畫面」在鍵面上做不到。

> **契約變更**：schema 目前的 mood enum 是 `idle | focused | happy | tired`。需要**加上 `thinking`**；`tired` 在 MVP 不使用但保留。

### 10.2 素材從哪來

程式碼能做的是「把圖按順序推到鍵面」，**畫不出像素圖**。三個選項，建議 A：

- **A. 抓現成 CC0 素材（建議）** — 到 itch.io 或 OpenGameArt 搜 `chicken sprite sheet` / `chicken pixel art`，篩 CC0 或 CC-BY。雞是很常見的素材，一張 sheet 通常就含站立、走路、吃東西。下載後切成單格 PNG 縮到鍵面尺寸。**半小時內可完成，授權務必確認並在 demo 標註來源。**
- **B. 團隊自己畫** — 只有 11 格、64×64 像素風，一個下午能畫完，獨特性最高。有人想畫就選這個。
- **C. 程式生成** — 用幾何圖形拼一隻雞。省了找素材但很陽春，**不建議把 hackathon 時間花在這**。

**降級方案**：時間不夠就每個狀態一張靜態圖。動畫是加分，**狀態可辨識才是必要的** — 使用者要能瞄一眼鍵面就知道系統在做什麼。

---

## 11. 契約變更清單（開工前要先談定）

這是對 `docs/data-contracts.md` 的增補，四項而已：

1. **新增 `/analyze-page`** endpoint 與 request/response schema（§8.1）— ai-engine
2. **新增 ActionCommand type `BLOCK_PAGE`**，payload `{ tabId, reason }` — app + actions
3. **chicken mood enum 加上 `thinking`** — app + logitech + web
4. **app ↔ browser 的 WebSocket 訊息** `PAGE_VISIT` / `PAGE_VERDICT`（localhost 內部協定，不進 shared/schemas）— app + browser

不需要改的：`ExternalMessage`、`AIRequest`、`AIDecision`、`AnalyzeMessageRequest/Response`、`SessionContext`、`InputEvent` 的既有 type。

---

## 12. 技術風險（依該先驗的順序）

1. **`/analyze-page` 的模型能力與延遲** — `gemma3:1b` 讀 1000 字判斷相關性可能不夠。第一天就用真實網頁實測三種模型。這是**整個 MVP 最核心功能的成敗點**。
2. **鍵面更新頻率** — 7/8/9 要每秒更新三顆鍵。若 Logi SDK 撐不住，退路是秒數格只在最後一分鐘跳動，或直接顯示到「分」。第一天測。
3. **Gmail OAuth** — demo 用測試帳號 + unverified app 模式，**不要把正式審核排進時程**。
4. **第 6 個模組沒有主人** — extension 若沒人接，功能 1 就做不出來。開工前先決定。

---

## 13. 延伸功能（MVP 之後，依優先序）

1. **Slack** 作為第二個訊息來源（`source` enum 已經含 `slack`）
2. **Automation（3 號鍵）** — Off / Assist / Auto，`/draft-reply` 已有 skeleton，先做 Assist
3. **Google Calendar** — `ExternalEvent` 契約已經定義好
4. **Auto Mode** — `mode` enum 已含 `auto`；依目前開啟的軟體自動判斷任務
5. **工作節奏分析** — 用累積的 session 與 override 資料建議今天適合的長度
6. **應用程式層級阻擋** — `SessionContext.allowedApps` / `blockedApps` 已經預留欄位

---

## 14. 開放問題

1. **Chrome extension 由誰負責？** 建議 `src/web` 的人兼；若無人接，功能 1 必須從 MVP 拿掉。
2. Dialpad 按鈕負責「開始」之後，1 號鍵只剩「顯示狀態 + 長按結束」— 要不要釋出去做別的？
3. overlay 的「我真的需要」放行 5 分鐘合理嗎？還是改成「只放行這一頁」？
4. 任務設定放在 web dashboard 是否 OK？
5. `app` ↔ `web` 用 WebSocket 推播還是輪詢 `AppState`？
