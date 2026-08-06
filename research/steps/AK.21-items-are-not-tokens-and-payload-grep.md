# BƯỚC AK.21 — `items[]` không phải bảng token, và đường rẻ nhất còn lại là grep payload

Mẫu: `LordsMobileBot.exe`  
Ngày: 2026-08-06  
Tiếp nối: `AK.20` + `AK.20.1` (commit `772740a`)

---

## 0. Nhận toàn bộ bốn hiệu chỉnh của peer review

Cả bốn đều đúng và được áp dụng nguyên văn.

| # | Hiệu chỉnh | Trạng thái |
|---|---|---|
| 1 | `KEY` per-method chỉ là STRONG, không phải CONFIRMED | Nhận |
| 2 | `itemCount == 6` là bộ lọc thử nghiệm, không phải dữ kiện | Nhận, và §1 cho thấy nó còn sai hơn thế |
| 3 | "runtime oracle sẽ không bao giờ liệt kê hết corpus" quá tuyệt đối | Nhận, hạ thành §11 |
| 4 | Slot `+0xE0` → wrapper `0x18003EE80` → helper `0x180007850` | Nhận, RETRACTED phát biểu cũ |

Ghi thêm về hiệu chỉnh 4, vì đây là lỗi phương pháp chứ không chỉ lỗi kết luận: hai số cần thiết để suy ra chuỗi wrapper đã nằm sẵn cạnh nhau trong bảng tham chiếu từ AK.16 và AK.19.

```text
[0x1800739E8 + 0xE0] = 0x18003EE80
[RSP] tai helper     = 0x18003EEE7
hieu                 = 0x67
```

Kế hoạch AK.20B đề nghị đo lại bằng `dps @rsp L20` + `s -q ... L1000` một thứ đã suy ra được bằng một phép trừ. **Quy tắc mới: trước khi đề xuất bất kỳ phép đo nào, đối chiếu đề xuất với bảng tham chiếu hiện có.**

---

## 1. REFUTED — `KEY k → record.items[k-1]`

Peer review xếp phát biểu này là UNPROVEN. Sai mức: nó **bị bác**, bằng dữ liệu đã có trong sổ, không cần đo thêm.

### Lập luận A — biên độ `low24` (kín, không phụ thuộc giả định nào)

Đã ghi từ BƯỚC AI:

```text
items[] = (tag << 24) | low24
48324 item, 3039 gia tri low24 phan biet, low24 max = 0x2CD1
S1 (sig heap) size = 0x2CD8
```

Token thật của `KEY 1` cho method `0x060008E1` đã biết từ map `RegisterResolvedToken` tại `A9BFE:9B8`:

```text
key 1 -> 0x040088ED    RID = 0x0088ED = 35053
```

So với biên độ quan sát:

```text
35053 > 11473 = 0x2CD1
```

Không một item nào trong toàn bộ 48.324 item có thể mang RID `0x88ED`. Vậy `items[]` **không chứa token**.

### Lập luận B — phân bố tag (bổ trợ, có điều kiện)

Nếu `tag` dùng chung `KIND_TABLE = {1:0x01, 2:0x02, 3:0x04, 4:0x06, 5:0x0A}` thì sáu token của `0x060008E1` cần 2 item `tag4` và 2 item `tag5`. Toàn corpus:

```text
tag4  n=33   d=20
tag5  n=3    ca ba deu = 0x10d
```

Một method không thể tiêu thụ hai trong ba item `tag5` của cả assembly.

Điều kiện: lập luận này giả định `tag` dùng chung `KIND_TABLE`, chưa chứng minh. Lập luận A không cần giả định nào.

### Lập luận C — sổ chứng cứ đã tự gọi đúng tên

`S1` được ghi nguyên văn là **sig heap**, và `items[] = (tag<<24) | S1_OFFSET`. Nghĩa là `items[]` trỏ vào heap chữ ký. Mô hình đề xuất ở AK.20 mâu thuẫn với chính dòng mô tả đã ghi trong sổ.

### Kết luận

```text
REFUTED: KEY k -> record.items[k-1]
STRONG : items[] la bang chu ky / kieu cua record, khong phai bang token
OPEN   : mang token that nam o dau (xem muc 4)
```

---

## 2. STRONG — cột `nLocals` của `methods.csv` chính là `itemCount`

Struct v5 có đúng sáu trường trong header:

```c
u8  maxStack;  u24 codeSize;  u16 itemBytes;
u16 ehCount;   u16 itemCount; u16 ehDataSize;   // 12 byte
```

CSV có sáu cột: `recOff, maxStack, codeSize, nLocals, ehCount, ilOffset`. Ba cột khớp tên trực tiếp. `recOff` và `ilOffset` là dẫn xuất. Vậy `nLocals` chỉ có thể là `itemCount` hoặc `itemBytes/4`.

Đối chứng dương từ row 2272:

```text
recOff[2272] = 0x150c4
recOff[2273] = 0x150d0
hieu         = 0x0C = 12 = header, khong item, khong EH
nLocals[2272] = 0   khop
```

Row 2273 có `nLocals = 2`, nếu items là `u32` thì `itemBytes = 8`.

### Kiểm chứng offline toàn corpus (6 dòng, không debugger)

```python
import csv
rows = list(csv.DictReader(open(r"C:\hvm\methods.csv")))
h = lambda s: int(s, 16) if s.strip().lower().startswith("0x") else int(s)
bad = 0
for a, b in zip(rows, rows[1:]):
    gap = h(b["recOff"]) - h(a["recOff"])
    eh  = gap - 12 - 4 * int(a["nLocals"])
    if eh < 0 or (int(a["ehCount"]) == 0 and eh != 0):
        bad += 1
print("vi pham =", bad, "/", len(rows) - 1)
```

### Phán quyết đặt trước

- `bad == 0` → `nLocals == itemCount` **CONFIRMED**, và `ehDataSize` của mọi record suy ra được miễn phí từ CSV.
- `bad > 0` nhưng đổi sang `gap - 12 - int(nLocals)` cho `bad == 0` → `nLocals == itemBytes`.
- Cả hai đều sai → record không liên tục, mâu thuẫn với `coverage 100.00%` của BƯỚC AI; phải xem lại parser.

### Hệ quả trực tiếp

IL của `0x060008E1` không dùng local nào. Nếu `nLocals == itemCount` thì record của nó có `itemCount = 0`, tức **không có `items[]` nào cả**. Giả thuyết AK.20 chết lần thứ hai, theo một đường độc lập.

Và bộ lọc H1/H2 có thêm một điều kiện miễn phí: `recOff[i+1] - recOff[i] == 12`.

---

## 3. CONFIRMED (số học) — `ilOffset` là tổng dồn, không đọc từ record

```text
ilOffset[2272] = 0xFFDEC = 1048044
codeSize[2272] = 0x4E    = 78
ilOffset[2273] = 0xFFE3A = 1048122
1048044 + 78   = 1048122   khop chinh xac
```

Struct v5 không có trường `ilOffset`. Vậy cột này do exporter tự cộng dồn.

**Cảnh báo:** mọi phép nối dùng `ilOffset` làm khoá là **vòng tròn** cho tới khi nó được đối chiếu với payload thật.

**Cơ hội:** đúng vì nó vòng tròn nên nó là một dự đoán có thể sai. Nếu payload thật sự là IL nối liền theo thứ tự record thì `pl_full.bin[ilOffset : ilOffset+codeSize]` phải là body. Đó là mục 5.

---

## 4. STRONG — mảng token thật nằm trong payload, và có đủ chỗ

```text
sum(codeSize) = 0x2ED00E = 3067918
payload size  = 0x346C10 = 3435536
chenh lech    = 0x059C02 =  367618
367618 / 10960 = 33.5 byte / method
```

33,5 byte thừa mỗi method là đủ cho một header nhỏ cộng một mảng token ngắn. Với `0x060008E1` cần 6 token = 24 byte, cộng header — vừa khít khoảng này.

```text
STRONG: payload = [ header nho + mang token that + IL body ] * 10960
```

Đây là giả thuyết thay thế cho mô hình `items[]` vừa bị bác ở §1, và nó **có thể kiểm bằng một lệnh grep**.

---

## 5. AK.21A — grep payload ★ ưu tiên 1, không cần debugger

IL của `0x060008E1` chứa một mỏ neo lý tưởng: lệnh chết `IL_0016 call 0x5F39B97D`. Đây là 5 byte literal nằm trong nhánh không bao giờ chạy tới, nên **không có lý do gì để bị relocate hay vá lúc chạy**.

```python
import re
p = open(r"C:\hvm\pl_full.bin", "rb").read()

pats = {
  "junk call 0x5F39B97D": bytes.fromhex("287DB9395F"),
  "duoi 6 vtoken"       : bytes.fromhex("2804008006280500800602280600800A002A"),
  "prefix 20 byte"      : bytes.fromhex("007F01008004FE1602008001 6F030080 0A2D0100".replace(" ", "")),
}
for name, pat in pats.items():
    hits = [m.start() for m in re.finditer(re.escape(pat), p)]
    print(f"{name:22s} n={len(hits):5d}  {[hex(x) for x in hits[:8]]}")

# khung lenh, bo qua 4 byte token
skel = re.compile(rb"\x00\x7f....\xfe\x16....\x6f....\x2d\x01\x00\x2b\x05\x28\x7d\xb9\x39\x5f", re.S)
print("khung lenh n=", [hex(m.start()) for m in skel.finditer(p)][:8])
```

Nếu tìm được `junk` tại `X`:

```python
base = X - 0x16                      # dau body
print("body base =", hex(base))
print("64 byte truoc :", p[base-0x40:base].hex(" "))
print("body 0x2D byte:", p[base:base+0x2D].hex(" "))

import csv
rows = list(csv.DictReader(open(r"C:\hvm\methods.csv")))
h = lambda s: int(s, 16) if s.strip().lower().startswith("0x") else int(s)
idx = {h(r["ilOffset"]): i for i, r in enumerate(rows)}
print("row khop ilOffset:", idx.get(base, "KHONG CO"))
```

### Phán quyết đặt trước

| Kết quả | Kết luận |
|---|---|
| `junk` trúng đúng 1 lần, `base` có trong cột `ilOffset` | **Ánh xạ RID ↔ record index ĐÓNG**, và H1/H2 trả lời luôn bằng `codeSize` của row đó |
| `junk` trúng, `base` không có trong `ilOffset` | Payload không xếp theo thứ tự record; cần bảng offset riêng — tìm nó trong S2 |
| `junk` + `duoi 6 vtoken` cùng trúng | **Virtual token được LƯU trong payload**, không sinh lúc chạy → rebuilder offline chỉ còn thiếu bảng KEY→token |
| `junk` trúng, `duoi` không trúng, `khung lenh` trúng | Ô token bị vá lúc chạy → đọc 64 byte trước `base` để tìm mảng token thật (§4) |
| `junk` không trúng | Payload không lưu IL dạng thô → mỗi body bị nén/mã hoá riêng → quay lại AK.21C |

Chi phí: một lần chạy Python, không debugger, không TTD. **Đây là lệnh có tỉ lệ thông tin trên chi phí cao nhất còn lại trong toàn dự án.**

---

## 6. `0x060008E1` dùng tiny method header

```text
codeSize = 0x2D = 45 < 64
maxStack = 8
nLocals  = 0
ehCount  = 0
```

Đúng chữ ký của `CorILMethod_TinyFormat`, nơi `maxStack` luôn ngầm định bằng 8.

Hệ quả cho bộ lọc: `maxStack == 8` **không phải** một điều kiện độc lập, nó bị suy ra từ ba điều kiện kia. Bộ lọc thật là `codeSize` + `nLocals == 0` + `ehCount == 0` + `gap == 12`.

Lưu ý ngược: histogram đã ghi `maxStack 8 -> 307 record` trên 10.960. Với một assembly .NET bình thường, tỉ lệ tiny method thường cao hơn nhiều. Hai khả năng, chưa phân biệt được:

- HVM lưu `maxStack` đã tính lại chứ không phải giá trị header, và 8 ở đây là trùng hợp;
- hoặc corpus này thật sự ít tiny method.

Dù sao 307 là một tập ứng viên nhỏ, nên phép quét sẽ dứt khoát.

---

## 7. Khung wrapper đóng hoàn toàn — bốn quan sát bổ sung

### 7.1 CONFIRMED — `11CA36:26C` và `11A7E4:8EC` là **cùng một lần gọi**

Điều này cần chứng minh, vì nếu là hai lần gọi khác nhau thì toàn bộ phép so sánh trước/sau ở AK.20.1 vô hiệu.

```text
prologue:      push rdi ; sub rsp,40h
RSP tai helper entry (11A7E4:8EC) = 0xFD8257AC88, [RSP] = 0x18003EEE7
RSP tai 11CA36:26C                = 0xFD8257AC90 = 0xFD8257AC88 + 8   (sau ret)
RBX tai 11CA36:26C                = 0x2410066E718 = dung proxy do
```

Hai điều kiện độc lập cùng khớp → cùng frame, cùng call. Cửa sổ `0x2252` sequence là thời gian helper chạy.

### 7.2 CONFIRMED — người gọi wrapper là **clrjit**, gọi trực tiếp

```text
RSP sau prologue = 0xFD8257AC90
RSP tai entry    = 0xFD8257AC90 + 0x40 + 8 = 0xFD8257ACD8
[0xFD8257ACD8]   = 0x00007FFAD4EA841F
clrjit base      = 0x00007FFAD4E10000
=> clrjit + 0x9841F
```

Wrapper được clrjit gọi trực tiếp qua vtable proxy, không phải HVM tự gọi nội bộ. Kết hợp với việc nhánh non-virtual delegate sang **cùng offset `+0xE0`** của object nền tại `[proxy+0x328]`, định danh không còn chỗ cho nghi ngờ:

```text
CONFIRMED: [0x1800739E8 + 0xE0] = ICorJitInfo::resolveToken
CONFIRMED: this = proxy base 0x2410066E718 (vtable thu nhat)
```

Một proxy chuyển tiếp slot N sang slot N của object nền, theo định nghĩa, là cùng một method. Đây mạnh hơn mức STRONG mà AK.20.1 gán.

### 7.3 CONFIRMED — `R8` của helper là địa chỉ trả về của chính wrapper

```text
mov r8, [rsp+48h]   ; 0xFD8257AC90 + 0x48 = 0xFD8257ACD8 = o chua return address
```

Giải thích trọn vẹn `R8 = 0x7FFAD4EA841F` đã ghi ở AK.19 mà khi đó chưa lý giải được. HVM truyền call-site của JIT cho helper — nhiều khả năng để phân biệt ngữ cảnh gọi.

### 7.4 STRONG — con trỏ IL của JIT nằm trong frame, đúng vị trí toán hạng

```text
[0xFD8257ACE0] = 0x24100669E12
ILCode cua 0x060008E1 = 0x24100669E10
chenh lech = 2 = vi tri toan hang cua  7F 01 00 80 04  (opcode +1, operand +2)
```

Ô này là home slot chưa được caller ghi, tức giá trị còn sót lại từ frame clrjit. Nhưng giá trị đúng bằng `ILCode + 2` xác nhận lần `resolveToken` này phục vụ `IL_0001 ldsflda 0x04800001` của `0x060008E1`. Xếp STRONG vì là ô sót, không phải ô được ghi có chủ đích.

### 7.5 Đường UserString — hệ quả cho rebuilder

```text
cmp eax, 70000000h        ; token la UserString
jne  kiem_tra_virtual_bit
cmp qword ptr [rbx+78h],0
jne  di_toi_helper        ; UserString + co flag -> luon qua helper
```

String literal cũng đi qua helper khi `[proxy+0x78] != 0`. Nghĩa là **`#US` heap cũng bị bảo vệ**. Rebuilder offline sẽ cần thêm nguồn cho chuỗi, không chỉ token. Chưa có mẫu nào, ghi vào backlog.

---

## 8. RETRACTED — dự đoán "`RAX` hoặc stack chứa `0x040088ED`"

Dự đoán này của AK.20B sai về mặt cấu trúc, không phải sai vì đo hụt.

```c
struct CORINFO_RESOLVED_TOKEN {
  +0x00 tokenContext    // input
  +0x08 tokenScope      // input
  +0x10 token           // input   <- 0x04800001 nam o day
  +0x14 tokenType       // input
  +0x18 hClass          // OUTPUT
  +0x20 hMethod         // OUTPUT
  +0x28 hField          // OUTPUT
  +0x30 pTypeSpec  +0x38 cbTypeSpec
  +0x40 pMethodSpec +0x48 cbMethodSpec
};                      // 0x50 byte
```

`resolveToken` trả về **handle runtime**, không bao giờ trả về metadata token. `s -d @rsp L100 040088ed` trượt là kết quả **bắt buộc**, kể cả khi mapping hoàn toàn đúng. Một phép đo mà cả hai kết quả đều không phân biệt được gì thì không phải phép đo.

Hai sửa nhỏ cho AK.20.1:

- `token` ở `+0x10` xác nhận struct này đúng là `CORINFO_RESOLVED_TOKEN` — thêm một bằng chứng cho §7.2.
- Cửa sổ `MemoryForPositionRange` nên là `0xFD8257ADD0 → 0xFD8257AE20` (0x50 byte), không phải `0xFD8257AE10` (0x40), nếu không sẽ bỏ sót `pMethodSpec` / `cbMethodSpec`.

---

## 9. 3/6 KEY của method này **đã biết**, ghi từ `A9BFE:9B8`

Các node map `RegisterResolvedToken` đã ghi trong sổ từ trước:

```text
key 1 -> 0x040088ED   @ 0x24100668CE0
key 2 -> 0x010004C9   @ 0x24100668D10
key 3 -> 0x0A001D99   @ 0x24100668D40
```

Cửa sổ emit `A9BD9 → A9BFE` sinh ra đúng 20 byte prefix `IL[0x00,0x14)` của `0x060008E1`, với ba token ảo `0x04800001 / 0x01800002 / 0x0A800003`. Vậy ba node trên **chính là** KEY 1..3 của method này.

Đối chứng ngữ nghĩa: `[B]` làm `stsfld 0x040088ED`, `0x060008E1` làm `ldsflda 0x04800001`. Cùng một field. `[B]` khởi tạo field guard, prologue của method bảo vệ đọc địa chỉ field đó. Nhất quán.

**Hệ quả:** AK.20B không cần đi săn lại mapping đầu tiên. Việc cần làm là đọc chính cái map đó.

### AK.21B — duyệt map thay vì săn resolver

```text
$$ std::map root, ghi tu BUOC AI: ctx+0x08 = _Myhead 0x24100668DA0
dq 0x24100668DA0 L4
dps 0x24100668CE0 L8
dps 0x24100668D10 L8
```

Phán quyết đặt trước:

- Map còn sống tại `11CA36:26C` và chứa `key1 -> 0x040088ED` → **map chính là bảng KEY→token**, và nó dump được trực tiếp. Đây là lối tắt tới mục tiêu A.
- Map bị xoá giữa các method → **KEY là ordinal cục bộ CONFIRMED** (vì scope bị reset), đúng như giả thuyết STRONG hiện tại.
- Map chứa hàng nghìn node → KEY phải được khoá kèm định danh method; tìm trường khoá thứ hai trong node.

Ba nhánh này phân biệt được câu hỏi global-vs-per-method mà peer review nêu ở mục 1, bằng **một** lệnh, thay vì sáu lần `bp` + `g`.

---

## 10. AK.21C — đếm số dòng MethodDef của host (offline)

Câu hỏi mở sau khi `RID == row index` bị bác: record được đánh chỉ số theo gì?

Giả thuyết ưu tiên: **chỉ số dày đặc chỉ trên tập method được bảo vệ**. Nếu host có nhiều hơn 10.960 MethodDef thì record index không thể bằng RID theo định nghĩa.

Đọc row count từ stream `#~` của `LordsMobileBot.exe` bằng Python thuần, không cần debugger, không cần CLR.

Phán quyết đặt trước:

- `MethodDef rows > 10960` → record chỉ phủ tập bảo vệ; cần một bảng `RID → recordIndex`, khả năng cao nằm trong S2 (`0x185E08`, 6125 slice).
- `MethodDef rows == 10960` → thứ tự record không theo RID, phải là hoán vị; tìm khoá sắp xếp khác.
- `MethodDef rows < 10960` → record không tương ứng 1-1 với method; mô hình v5 cần xem lại.

---

## 11. Điểm chiến lược — phát biểu lại cho đúng mức

Phát biểu cũ ở AK.20:

```text
Runtime oracle se khong bao gio liet ke het corpus, ke ca chay nhieu lan.
```

RETRACTED. Dữ kiện chỉ chứng minh được độ phủ **của trace này**:

```text
82 site43 compile events / 10960 records = 0.748%
```

Phát biểu đúng mức:

```text
Do phu runtime hien tai rat thap.
Force-JIT co the nang do phu dang ke, nhung co lo hong da biet:
  abstract, P/Invoke, open generic (ChatGPT neu tu M2, van dung).
Do do runtime enumeration khong the CHUNG MINH la day du,
nen no dung lam ORACLE doi chieu, khong dung lam NGUON duy nhat.
Metadata join van la duong chinh toi muc tieu offline.
```

Kết luận chiến lược không đổi; chỉ có lý do đỡ nó là đổi, từ một phát biểu tuyệt đối không chứng minh được sang một phát biểu về gánh nặng chứng minh.

---

## 12. Thứ tự ưu tiên sau AK.21

| Hạng | Việc | Chi phí | Đóng được gì |
|---|---|---|---|
| 1 | **AK.21A** grep `28 7D B9 39 5F` trong `pl_full.bin` | 1 script Python | RID↔record, H1/H2, token lưu hay sinh — cả ba cùng lúc |
| 2 | **§2** kiểm `nLocals == itemCount` trên 10.959 khe | 6 dòng Python | `itemCount` cho mọi record, `ehDataSize` miễn phí |
| 3 | **AK.21B** duyệt map `0x24100668DA0` | 3 lệnh WinDbg | global vs per-method, có thể là bảng KEY→token luôn |
| 4 | **AK.21C** đếm MethodDef rows | 1 script Python | Mô hình đánh chỉ số record |
| 5 | Nhánh C AK.20 — 5 mẫu site43 còn lại | 5 × 5 lệnh | Mẫu đối chứng cho mọi kết luận trên |
| — | Wide static census `[0x180007000, 0x180040000)` | | Tụt hạng, không nằm trên đường tới mục tiêu offline |

Hạng 1, 2, 4 chạy được **cùng lúc và không cần debugger**. Chỉ hạng 3 và 5 cần TTD.

---

## Tóm tắt trạng thái bằng chứng sau AK.21

```text
CONFIRMED
  CORINFO_METHOD_INFO +18/+1C/+20/+24 = ILCodeSize/maxStack/EHcount/options
  [0x1800739E8 + 0xE0] = ICorJitInfo::resolveToken, wrapper 0x18003EE80
  0x180007850 la helper noi bo cua wrapper, khong phai slot vtable
  11CA36:26C va 11A7E4:8EC la cung mot lan goi
  Nguoi goi wrapper la clrjit+0x9841F, goi truc tiep qua vtable proxy
  R8 cua helper = dia chi tra ve cua wrapper
  ilOffset trong methods.csv la tong don, khong doc tu record
  0x060008E1 dung keys 1..6 lien tuc theo thu tu IL

STRONG
  KEY la ordinal cuc bo theo method
  nLocals (CSV) == itemCount (struct v5)
  items[] la bang chu ky, tro vao S1
  Mang token that nam trong payload, ngan sach 0x59C02
  Con tro IL cua JIT = ILCode+2 trong frame

UNPROVEN
  Record cua 0x060008E1 nam o row nao
  Payload co xep theo thu tu record khong
  Virtual token duoc luu hay sinh luc chay
  Bang RID -> recordIndex nam o dau

RETRACTED trong buoc nay
  KEY k -> record.items[k-1]                        (REFUTED, muc 1)
  RAX hoac stack se chua 0x040088ED                 (muc 8)
  Runtime oracle khong bao gio phu het corpus       (muc 11)
  Slot +0xE0 khong thuoc token-resolution path      (da rut o AK.20.1, xac nhan lai)
  Ke hoach AK.20B dung dps @rsp de tim slot vtable  (thua, muc 0)
```
