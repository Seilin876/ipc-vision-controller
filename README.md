# IPC Vision Controller

標籤料帶的檢測控制程式。進給料帶、觸發 Keyence SR-X300 讀碼、判定條碼與印刷品質等級，並把結果寫進追溯資料庫。

Windows / .NET 8 / WinForms。

---

## 硬體架構

```
                ┌─ Keyence SR-X300 讀碼器 ──────── 乙太網（TCP 無協定，讀碼器為伺服器）
工控機 (Windows) ┤
                └─ PCI-L221B1D0 ── EtherCAT ── R1-EC5500D1 ── R1-EC5621D1 ── DR57A ── C-57STM04
                   主站卡                        耦合器        脈波輸出模組   步進驅動器  步進馬達
```

| 裝置 | 角色 | 目前狀態 |
|---|---|---|
| Keyence SR-X300 | 讀碼與 ISO 印刷品質等級 | **已接上並驗證** |
| Keyence IV4 | 字符檢測（OCR） | 尚未導入 |
| PCI-L221B1D0 + R1 模組 + DR57A | 料帶進給 | 已接線，**軟體尚未實作驅動**，目前跑模擬 |

進給軸是**開環脈波輸出**，不是伺服軸 —— 沒有編碼器回授。詳見下方「已知問題」。

---

## 快速開始

### 建置

```
dotnet build IpcVisionController.sln -c Release
dotnet test tests/IpcVisionController.Core.Tests
```

執行檔在 `src\IpcVisionController.App\bin\Release\net8.0-windows\IpcVisionController.exe`。

### 設定

執行檔旁需要一個 `device.json`，把 `device.sample.json` 複製過去改。**檔案不存在時程式會拒絕啟動並顯示它找過的路徑** —— 它不會退回模擬，因為模擬判定在畫面上與實機結果分不出來。

目前現場的最小設定：

```json
{
  "CodeReader": {
    "Host": "192.168.1.10",
    "Port": 9004,
    "TriggerCommand": "LON",
    "Terminator": "Cr",
    "CodeField": 0,
    "GradeField": 1
  }
}
```

其餘欄位的預設值已經是實機實測值，可以省略。

沒有裝置而只想看畫面時：

```
IpcVisionController.exe --mock
```

標題列會標示「【模擬資料 MOCK DATA】」。那些判定是產生器產出的，**不是量測結果**。

### 配方

在主畫面「配方 Recipe」列設定，按「儲存配方」寫入 `recipe.json`：

| 欄位 | 說明 |
|---|---|
| 機種 Model | 追溯紀錄的機種名稱 |
| 條碼筆數 Codes | 一次觸發應讀到幾筆（六道並排 = 6） |
| 長度 Length | 條碼字元數，**0 = 不檢查** |
| 檢查等級 Check grade | ISO 15415/15416 等級下限，刻度 0–4（4 最好） |
| 字符區域 Regions | **0 = 不檢查**（IV4 未導入時必須填 0） |
| 一格脈波 Pulses/feed | 一次進給的脈波數 |

---

## 操作流程

1. **初始化** — 連線裝置、致能驅動器、進給軸歸零、載入配方
2. **單次觸發** — 進給一格並檢測一次，用於試機
3. **開始 / 停止** — 連續生產
4. **解除故障** — 故障後回到離線狀態，**必須重新初始化**

### 判退與故障的分野

這是整支程式最重要的一條界線：

- **工件不良** → 判退，繼續生產。條碼讀不到、長度不符、等級不足都屬於這一類。
- **設備故障** → 停線。連線失敗、裝置逾時、回報異常電文屬於這一類。

讀碼器讀不到時輸出 `ERROR`，那是**工件不良**。因為 `ERROR` 以 `ER` 開頭，`ErrorPrefix` 預設為空字串（停用），並把 `ERROR` 列入 `EmptyTokens` —— 否則一張標籤漏貼就會停掉整條線。

### 三層逾時的順序

必須是 **讀碼器 < `ResponseTimeoutMs` < `CodeReadTimeout`**：

| 層 | 位置 | 目前 | 作用 |
|---|---|---|---|
| 讀取時限 | SR-X300 端 | 800 ms | 讀不到就回答，讓它變成判退 |
| `ResponseTimeoutMs` | `device.json` | 2000 ms | 裝置真的沉默才觸發 |
| `CodeReadTimeout` | 程式內建 | 3000 ms | 最外層保護 |

沒有讀碼器那一層的話，「一張標籤漏貼」會走到程式的逾時 → 判成設備故障 → 停線。

---

## 已知問題

### 位置偏移會累積（開環進給，無定位標記）

**這是目前最需要注意的限制。**

進給軸是開環脈波輸出：脈波模組只知道自己送出了幾個脈波，**不知道馬達實際走了多少**。系統裡沒有任何東西會回頭確認料帶到底走了多遠。

誤差來源：

- 步進馬達失步（負載變動、加速太急）
- 料帶在滾輪上滑動
- 印刷的實際標籤間距與設定的「一格脈波」有微小差異

**單次誤差很小，但它會累加。** 跑幾百格之後，標籤會逐漸漂出讀碼器的視野。

**症狀特別容易誤判**：機器跑了一陣子之後「突然開始每批都判退」，看起來像讀碼器壞了、像印刷變差、像鏡頭髒了 —— 但真正的原因是標籤已經不在視野正中央。

**為什麼沒有修正機制**：一般做法是在料帶上印**定位標記**（色標），或用標籤間隙，配一個光電感測器；每一格「進給到標記為止」而不是「進給固定脈波數」，累積誤差每格歸零。本機構**沒有安裝這個感測器**，所以沒有任何自動修正。

因此：

- 程式的「初始化」只會把脈波計數**歸零**，不會對齊任何東西。訊息寫的是「進給軸歸零」而不是「對齊定位標記」—— 那是刻意的，程式不該聲稱一件沒有發生的事。
- 偏移**必須人工介入**：定期目視確認標籤是否還在視野中央，需要時手動重新對位。

**目前的處理方式：以人工介入為主。**

若這個問題頻繁造成異常，預計的下一步是讓 SR-X300 回傳條碼的**位置座標**（設定軟體的附加數據裡有「條碼中心座標」），用它當作軟性的定位參考來修正偏移 —— 那樣就不必增加硬體感測器。此功能目前**未實作**。

### 一筆紀錄涵蓋六張標籤

一次觸發讀到六筆條碼，但資料庫存的是**一次觸發一筆紀錄**。所以任一張判退會讓整列六張判退。

紀錄列表已改成「一筆條碼一列」，「位置 #」欄位對應實體位置（讀不到的那一道會**缺號**），所以人工可以只挑出那一張。但資料上仍是一列一個判定，良率也是按觸發次數計算，不是按標籤張數。

要逐張判定的話是**紀錄粒度**的改動（一筆條碼一筆紀錄），會連帶改變良率的意義與資料庫結構。目前未做。

### 進給軸實機驅動尚未實作

`IMotorController` 目前只有 `MockMotorController` 一個實作。EtherCAT SDK（`EtherCAT/` 資料夾）已經確認可用，API 與介面對得上：

| 介面 | Delta SDK |
|---|---|
| `ConnectAsync` | `CS_ECAT_Master_Open` → `Get_CardSeq` → `Master_Initial` → 輪詢 `Check_Initial_Done` |
| `EnableAsync` | `CS_ECAT_Slave_Motion_Set_Svon` |
| `FeedAsync` | `CS_ECAT_Slave_PP_Start_Move`（相對）+ 輪詢 `Get_Mdone` |
| `HomeAsync` | `CS_ECAT_Slave_Motion_Set_Position(0)` |
| `StopAsync` | `CS_ECAT_Slave_Motion_Sd_Stop`（減速停止，不用 `Emg_Stop`） |
| `CurrentPosition` | `CS_ECAT_Slave_Motion_Get_Position` |

實作前還需要決定兩件事：

1. **加減速度**：`PP_Start_Move` 需要，但 `FeedAsync` 沒有這兩個參數。它屬於機構特性，應由驅動實作自己的設定提供（`device.json` 加一段 `FeedAxis`），不該加進介面。開環步進的加速太急就會失步。
2. **32 位元計數繞回**：SDK 的位置是 `int`，而介面宣告 `long`。沒有定位標記可週期性歸零時，計數只會單向累加，約 21 億脈波後溢位。實機實作必須在軟體端以 `long` 累計並處理繞回。

**在實機驅動接上之前，畫面上的「已進給」數字是模擬的。** 標題列的實機標示目前只涵蓋讀碼器。

---

## 專案結構

```
src/IpcVisionController.Core/     商業邏輯，不含 UI
  Hal/                            裝置抽象與驅動
    IDevice.cs                    IMotorController / ICodeReader / ICharacterVerifier
    NonProtocolSensor.cs          共用的 TCP 連線層（連線、組框、逾時）
    SrX300CodeReader.cs           SR-X300 讀碼器
    Iv4CharacterVerifier.cs       IV4 字符檢測（格式未經實機驗證）
    CodeFrameReader.cs            電文 → 條碼結果（純函式）
    Mock*.cs                      測試替身，同時供 --mock 使用
  Machine/
    InspectionSequencer.cs        狀態機與檢測週期
    LabelJudge.cs                 判定規則（唯一決定 PASS/FAIL 的地方）
  Models/  Data/  Recipes/

src/IpcVisionController.App/      WinForms 外殼
  Program.cs                      組裝根，唯一決定接實機或模擬的地方
  MainForm.cs                     操作畫面

tests/IpcVisionController.Core.Tests/    228 項
EtherCAT/                         Delta EtherCAT SDK 與手冊（非本專案程式碼）
```

### 設定檔怎麼分

| 檔案 | 內容 | 什麼時候變 |
|---|---|---|
| `device.json` | 這台機器接了哪些裝置、裝在哪 | 機構改變時 |
| `recipe.json` | 這個機種怎麼判、一格走多遠 | 每次換線 |

分開的理由：混在一起的話，換線就會誤改機構參數。

---

## 開發慣例

- 註解為 zh-TW / EN 雙語，**請保留**。註解重點在「為什麼」而不是「做什麼」。
- 測試是主要文件。電文解析的測資取自實機擷取，不用想像的格式。
- 原始資料（`*.xlsx`、`*.csv`、`*.parquet`）與 `device.json`、`recipe.json`、`inspection.db` 不進版控。
