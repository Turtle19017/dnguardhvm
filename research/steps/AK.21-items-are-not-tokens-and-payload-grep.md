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

---

## 13. AK.21.1 — Kết quả sau phép đo: raw grep âm tính, XOR token và cache lineage

Phần này là phụ lục hậu nghiệm của AK.21. Các kết luận tại đây **supersede** những giả thuyết đặt trước ở §4, §5, §6 và thứ tự ưu tiên cũ ở §12. Không xoá kết luận cũ; mọi điểm sai được đánh dấu RETRACTED ngay dưới phép đo đã bác nó.

### 13.1 CONFIRMED — raw body của `0x060008E1` không nằm nguyên văn trong payload

Bốn phép grep trên `pl_full.bin` đều trả về 0 hit:

```text
junk_call:    count=0    # 28 7D B9 39 5F
suffix19:     count=0    # 19-byte runtime suffix
virtual_tail: count=0    # tail chứa KEY 4..6
full_body_2d: count=0    # full runtime body 0x2D
```

Cũng không tìm thấy raw literal `mask` hay ba masked-real token đã biết trong các artifact tĩnh:

```text
0x6A714B62  mask
0x6E71C38F  KEY1 masked-real
0x6B714FAB  KEY2 masked-real
0x607156FB  KEY3 masked-real candidate
```

Các file đã quét:

```text
pl_full.bin       size 0x346C10
s2.bin            size 0x185E08
md_full.bin       size 0x1E06E8
LordsMobileBot.exe size 0x0D390628
```

Phán quyết:

```text
CONFIRMED: Bốn byte-string của body runtime mẫu không tồn tại nguyên văn trong pl_full.bin.
CONFIRMED: Bốn DWORD runtime trên không tồn tại nguyên văn trong bốn artifact đã quét.
RETRACTED: payload = [header nhỏ + mảng token thật + raw IL body] là STRONG.
RETRACTED: H1 raw full-body 0x2D và H2 raw suffix 0x19 là hai mô hình lưu trữ còn sống.
STRONG: payload dùng representation encoded/structured hoặc body được dựng từ nhiều nguồn.
```

Không mở rộng thành “không method nào có raw IL”; phép đo chỉ kín cho các anchor của method `0x060008E1`.

### 13.2 Census `codeSize`: H1/H2 có candidate nhưng không thể join bằng header runtime

Census sửa lỗi PowerShell alias `H = Get-History` cho kết quả:

```text
codeSize 0x2D: 71 record, trong đó EH=0 là 69
codeSize 0x19: 31 record, tất cả EH=0
```

Không record nào trong hai tập có `maxStack=8`.

```text
CONFIRMED: bộ lọc codeSize + maxStack=8 + EH=0 là sai.
RETRACTED: candidate count bằng 0.
RETRACTED: methods.csv.maxStack phải bằng CORINFO_METHOD_INFO.maxStack của body runtime.
UNPROVEN: record tương ứng của 0x060008E1 và ý nghĩa 0x2D/0x19 trong record layer.
```

### 13.3 CONFIRMED — `CORINFO_RESOLVED_TOKEN` được điền bằng block copy 0x38 byte

Tại resolver return, HVM copy chính xác 0x38 byte từ temporary result vào `CORINFO_RESOLVED_TOKEN + 0x18`:

```text
source      = 0xFD8257AB08
destination = 0xFD8257ADE8
count       = 0x38
capacity    = 0x38
```

Khối bảy qword tương ứng:

```text
+0x18 hClass
+0x20 hMethod
+0x28 hField
+0x30 pTypeSpec
+0x38 cbTypeSpec + padding
+0x40 pMethodSpec
+0x48 cbMethodSpec + padding
```

`0x18004759B` chỉ là inner memmove loop; caller `0x1803793A7..0x1803793D0` chuẩn bị source `[rsp+48]` rồi gọi secure-copy wrapper `0x180460E0`.

```text
RETRACTED: copy length chỉ là 0x28.
RETRACTED: các write tại 0x18004759B là assignment semantic cho từng handle field.
```

### 13.4 CONFIRMED — cache resolved handles có khóa `(module, masked-real token)`

Cache node mẫu tại `0x24100668B90`:

```text
+0x00/+0x08/+0x10  tree links
+0x18              module/scope = 0x7FFA74E9E0A0
+0x20              key32        = 0x6E71C38F
+0x28              hClass       = 0x7FFA7500C700
+0x30              hMethod      = 0
+0x38              hField       = 0x7FFA74FC4198
```

Đường runtime:

```text
0x1800021B0  lookup cache
0x1800058A0  insert cache miss result
```

`0x1800058A0` duyệt cây theo cặp `(module, key32)` và copy triple `hClass/hMethod/hField` vào value của node. Nó không resolve metadata token; việc resolve đã xảy ra trước đó.

```text
CONFIRMED: đây là cache runtime-handle, không phải map u32 virtual-key → u32 real-token.
RETRACTED: object 0x24100668B90 có thể đồng nhất với std::map KEY→real token cũ.
```

### 13.5 CONFIRMED — exact bridge masked-real token → CLR token → CoreCLR handles

Ở cache-miss path, request mutable trước/ sau bridge:

```text
before:
  token     = 0x6E71C38F
  flags     = 0x00000004

after:
  token     = 0x040088ED
  flags     = 0x00000004
  hClass    = 0x7FFA7500C700
  hMethod   = 0
  hField    = 0x7FFA74FC4198
```

Exact writer giải token:

```asm
0x1803940AC  xor dword ptr [rbx+10h], r9d
```

Với KEY 1:

```text
0x6E71C38F XOR 0x6A714B62 = 0x040088ED
```

Sau XOR, HVM delegate cùng `CORINFO_RESOLVED_TOKEN*` sang object nền tại vtable slot `+0xE0`. Data breakpoint dừng trực tiếp trong:

```text
coreclr!CEEInfo::resolveToken+0x3A8
mov [rdi+28h],rax
```

và ghi `hField = 0x7FFA74FC4198`.

Chuỗi end-to-end cho KEY 1:

```text
virtual token 0x04800001
  → real CLR token 0x040088ED đã tồn tại trong resolver state/register
  → masked-real 0x6E71C38F làm cache key
  → XOR mask 0x6A714B62 khi cache miss
  → CLR token 0x040088ED
  → real CoreCLR CEEInfo::resolveToken
  → hClass 0x7FFA7500C700 / hField 0x7FFA74FC4198
```

### 13.6 CONFIRMED — KEY 2 dùng cùng mask

Tại event `11CA3E:1A0`:

```text
before token = 0x6B714FAB
mask         = 0x6A714B62
flags        = 0x00000101
after token  = 0x010004C9
```

Phép tính:

```text
0x6B714FAB XOR 0x6A714B62 = 0x010004C9
```

Đây đúng là real `TypeRef` token đã biết của KEY 2.

```text
CONFIRMED: cùng mask dùng cho KEY 1 FieldDef và KEY 2 TypeRef trong transaction này.
STRONG: flags 0x101 chứa usage/context bits của constrained TypeRef, không chỉ metadata table kind.
```

### 13.7 RETRACTED — `0x6EF14B63` không phải masked-virtual lookup key

Trong JIT resolver invocation, exact XOR instruction cũng chạy trên raw virtual token:

```text
0x04800001 XOR 0x6A714B62 = 0x6EF14B63
```

Nhưng read watchpoint trên slot chứa `0x6EF14B63` không dừng; continuation `0x18003EEE7` thắng trước. Giá trị này không được đọc lại từ slot trước khi helper return.

```text
RETRACTED: có map 0x6EF14B63 → 0x6E71C38F.
RETRACTED: 0x6EF14B63 là lookup key semantic.
STRONG: lần XOR raw virtual token là mutation/scrub của temporary request ở đường khác với cache-miss decode.
```

### 13.8 CONFIRMED — raw virtual token bị ghi đè trực tiếp bằng masked-real token trước cache lookup

Slot source ban đầu chứa `0x04800001`, rồi exact writer:

```asm
0x18037928E  mov dword ptr [rsp+150h],r8d
```

ghi:

```text
R8D  = 0x6E71C38F
R14D = 0x040088ED
```

Sau đó qword:

```text
0x00000004_6E71C38F
```

được generic copy routine `0x18047390` copy vào mutable request. Đường cache hit/miss bắt đầu sau bước này.

```text
CONFIRMED: virtual 0x04800001 → masked-real 0x6E71C38F xảy ra trước cache lookup.
CONFIRMED: R14D đồng thời chứa real CLR token 0x040088ED.
STRONG: R8D = R14D XOR resolverMask.
UNPROVEN: exact instruction tạo R8D và exact source tạo R14D.
```

### 13.9 CONFIRMED — mask nằm trong resolver-state object và writer khởi tạo đã bắt được

Resolver-state object:

```text
state          = 0x24100666840
mask field     = state + 0x30
mask address   = 0x24100666870
mask value     = 0x6A714B62
```

Exact writer gần nhất trước lần dùng:

```text
TTD 8517:F94
0x18000F48A  mov dword ptr [rsi+30h],eax
RSI = 0x24100666840
EAX = 0x6A714B62
EDX = 0x6A714B62
```

Ngay sau đó code zero nhiều field và cấp phát object con 0xE0 byte, nên block này là initializer/constructor candidate của resolver state.

```text
CONFIRMED: mask được lưu vào resolver-state+0x30 tại 0x18000F48A.
CONFIRMED: 0x18000F48A chỉ lưu giá trị đã có; chưa phải generator.
STRONG: mask có scope resolver/proxy instance.
UNPROVEN: caller/source tạo EAX/EDX, scope giữa instance/process/build, và nguồn host/offline.
```

### 13.10 Pipeline runtime hiện tại

```text
virtual token (ví dụ 0x04800001)
    ↓ map/token-resolution stage chưa đóng exact producer
real CLR token (R14D = 0x040088ED)
    ↓ XOR resolverMask 0x6A714B62
masked-real token (R8D = 0x6E71C38F)
    ↓ cache lookup 0x1800021B0, khóa (module, masked-real)

cache hit:
    node +0x28/+0x30/+0x38 → hClass/hMethod/hField

cache miss:
    masked-real XOR resolverMask → real CLR token
    ↓ underlying CoreCLR CEEInfo::resolveToken
    ↓ cache insert 0x1800058A0

handles
    ↓ temporary 0x38-byte result
    ↓ secure copy
CORINFO_RESOLVED_TOKEN output
```

Lớp mask/cache phục vụ runtime handles. Offline rebuilder cần ưu tiên cạnh:

```text
virtual KEY → real CLR metadata token
```

và không cần mô phỏng cache handles nếu đã tái tạo được token thật.

### 13.11 Trạng thái canonical sau AK.21.1

```text
CONFIRMED
  raw full-body/suffix/virtual-tail/junk-call của 0x060008E1 không có trong pl_full.bin
  codeSize 0x2D có 71 record; 0x19 có 31 record
  resolver output copy dài 0x38 byte
  cache lookup 0x1800021B0 / insert 0x1800058A0
  cache key = (module, masked-real token)
  6E71C38F XOR 6A714B62 = 040088ED
  6B714FAB XOR 6A714B62 = 010004C9
  CoreCLR CEEInfo::resolveToken ghi runtime handles
  mask state+0x30 được ghi tại 0x18000F48A

STRONG
  R14D là real token từ virtual-token map
  R8D = realToken XOR resolverMask
  mask có scope resolver/proxy instance
  payload dùng encoded/structured representation

UNPROVEN
  exact producer virtual KEY → real token
  KEY 4..6 của 0x060008E1
  record tương ứng của method 0x060008E1
  nguồn host/offline của resolverMask
  representation payload và bảng RID → recordIndex

RETRACTED / REFUTED
  payload lưu raw full body 0x2D hoặc raw suffix 0x19
  payload = [header + token array + raw IL body] là STRONG
  maxStack CSV phải bằng maxStack runtime
  0x1803940AC luôn là token decoder semantic
  0x6EF14B63 là masked-virtual lookup key
  cache handle object là cùng map KEY→real token
  offline rebuilder phải tái tạo runtime handle cache
```

### 13.12 Thứ tự ưu tiên mới

| Hạng | Việc | Đóng được gì |
|---|---|---|
| 1 | Duyệt map `0x24100668DA0` và lấy KEY 4..6 | Hoàn thành oracle KEY→real token cho method mẫu |
| 2 | Kiểm toàn corpus `nLocals == itemCount` | Đóng record layout và EH size |
| 3 | Đếm MethodDef rows host | Phân biệt dense protected index / permutation |
| 4 | Tìm exact producer R14D/R8D | Đóng runtime KEY→real-token stage |
| 5 | Reverse representation payload/S2 theo method mẫu | Đường chính tới host-only decoder |
| 6 | Truy caller/source của initializer mask | Chuyển sang AK.22 chỉ khi dẫn tới nguồn host, không đào cache mechanics thêm |

---

## 14. AK.21.2 — Review fixes, record layer toàn corpus và exact KEY→token data-flow

Phần này là trạng thái canonical mới nhất và **supersede** các overclaim còn sót trong §1–§13. Không xoá lịch sử phép đo; các phát biểu cũ được sửa bằng mức bằng chứng rõ ràng dưới đây.

### 14.1 Sửa phạm vi kết luận `items[]`

Biên `low24 <= 0x2CD1` bác trực tiếp mô hình:

```text
KEY k → record.items[k-1] → real CLR token
```

Nhưng nó không bác việc signature trong S1 có thể chứa hoặc tham chiếu metadata token theo representation riêng.

```text
CONFIRMED: items[] không phải bảng KEY→real-token trực tiếp.
STRONG: items[] trỏ vào signature/type arena S1.
UNPROVEN: signature S1 có chứa hoặc tham chiếu metadata token hay không.
```

Lập luận phân bố `tag5` cũ không được dùng làm chứng minh: một method về nguyên tắc vẫn có thể chiếm hai trong ba item quan sát. Nó chỉ là thống kê corpus, không phải impossibility proof.

### 14.2 CONFIRMED — raw record header và CSV khớp toàn bộ 10.960 record

Validator đọc trực tiếp `md_full.bin` theo layout:

```text
recOff+0x00  u8  maxStack
recOff+0x01  u24 codeSize
recOff+0x04  u16 itemBytes
recOff+0x06  u16 ehCount
recOff+0x08  u16 itemCount
recOff+0x0A  u16 ehDataSize
```

Kết quả trên 10.960 row:

```text
CSV.maxStack == raw.maxStack                    mismatch 0
CSV.codeSize == raw.codeSize                    mismatch 0
CSV.nLocals  == raw.itemCount                   mismatch 0
CSV.ehCount  == raw.ehCount                     mismatch 0
raw.itemBytes == 4 * raw.itemCount              mismatch 0
nextRecOff-recOff == 12+itemBytes+ehDataSize    mismatch 0
record bounds                                     mismatch 0
```

Record cuối kết thúc chính xác tại:

```text
lastRecordEnd = 0x57C08 = S0 size
```

Do đó:

```text
CONFIRMED: CSV.nLocals chính là raw.itemCount trên toàn corpus.
CONFIRMED: raw.itemBytes = 4 * raw.itemCount trên toàn corpus.
CONFIRMED: recordSize = 12 + itemBytes + ehDataSize.
CONFIRMED: 10.960 record phân hoạch kín S0 [0,0x57C08).
```

Điều không được suy ra:

```text
UNPROVEN: record của 0x060008E1 có itemCount=0.
```

Không có opcode local không chứng minh signature-item list rỗng, và method vẫn chưa join với record cụ thể.

### 14.3 CONFIRMED — `ilOffset` là tổng dồn trên toàn corpus

Toàn bộ 10.959 quan hệ đều khớp:

```text
ilOffset[i+1] == ilOffset[i] + codeSize[i]
```

```text
CONFIRMED: ilOffset là cumulative codeSize do exporter tạo.
UNPROVEN: ilOffset là raw offset thật trong pl_full.bin.
```

### 14.4 Artifact provenance và positive controls

```text
md_full.bin
  size   0x1E06E8
  sha256 809333cb66fb64622e7af9f5f1d32836cbca3b19ba66f7c0c41d34caa0a62284

methods.csv
  size   0x4D76F
  sha256 49aa99ed2e1223577905e46b86902b0e194cf7fdb74dc93204166bbe14a3743a

pl_full.bin
  size   0x346C10
  sha256 1e007a5b90f5bf8afa2ea86c248a877cda6156233859fe35e5d9d4646e0ed3e7

s2.bin
  size   0x185E08
  sha256 190c4e8200e2cff577a3085a885f3fa7c99498e6559273fb9dcc153a0ea5ac25

LordsMobileBot.exe
  size   0x0D390628
  sha256 2178bc077a362a18dbdc5b141478740f0870fedc2774e2ed0d741c606f318a0e
```

Positive controls:

```text
md_full.bin first 16-byte anchor found at offset 0 exactly once
pl_full.bin first 16-byte anchor found at offset 0 exactly once
s2.bin first 16-byte anchor found at offset 0 exactly once
LordsMobileBot.exe MZ at 0, PE signature at file offset 0xF0
S2: 399234 / 399234 DWORD có observed tag nibble 0xA
```

Raw grep âm tính vẫn chỉ áp dụng cho các anchor của method `0x060008E1`; không tổng quát hoá sang toàn corpus.

### 14.5 Sửa mức bằng chứng của census `codeSize/maxStack`

Census quan sát:

```text
codeSize 0x2D: 71 record, 69 record EH=0
codeSize 0x19: 31 record, tất cả EH=0
không record nào trong hai tập đồng thời có maxStack=8
```

Phán quyết đúng:

```text
CONFIRMED: bộ lọc kết hợp codeSize∈{0x2D,0x19} + maxStack=8 + EH=0 trả 0 candidate.
UNPROVEN: codeSize giả định, runtime↔record maxStack equality, hay phép join là thành phần thất bại.
```

Không retract riêng `methods.csv.maxStack == raw.maxStack`; equality này đã được validator xác nhận toàn corpus. Chỉ còn chưa biết raw-record maxStack có khớp runtime method sample hay không vì chưa join record.

### 14.6 Tiny format và UserString — hạ về đúng mức

Tuple runtime:

```text
codeSize=0x2D, maxStack=8, EH=0, không thấy local opcode
```

chỉ **consistent with** tiny format. Fat header vẫn có thể biểu diễn cùng các giá trị.

```text
STRONG: sample tương thích CorILMethod_TinyFormat.
UNPROVEN: original protected MethodBody dùng tiny header.
```

Wrapper có một conditional custom path cho UserString khi `[proxy+0x78] != 0`.

```text
CONFIRMED: custom conditional UserString path tồn tại.
STRONG: DNGuard có thể bảo vệ/biến đổi #US qua path này.
UNPROVEN: sample UserString cụ thể và representation offline của #US.
```

### 14.7 Sửa phát biểu ABI của `resolveToken`

Metadata token không thuộc output ABI của `CORINFO_RESOLVED_TOKEN`; output là handles/spec blobs. Tuy nhiên helper nội bộ vẫn có thể giữ token trong register/state, và trace hiện tại đã thấy chính xác điều đó.

```text
CONFIRMED: metadata token không phải output ABI của resolveToken.
CONFIRMED: helper nội bộ giữ real CLR token trong EAX/R14D trước cache layer.
```

Vì vậy grep stack miss không bác mapping và cũng không phải kết quả “bắt buộc” theo nghĩa toàn bộ internal state không thể chứa token.

### 14.8 CONFIRMED — identity và layout của ordered KEY→real-token map

Tại `AD7BF:32`:

```text
head = 0x24100668DA0
head+0x00 = 0x24100668CE0   leftmost
head+0x08 = 0x24100668D10   root
head+0x10 = 0x24100668D40   rightmost
```

Node layout quan sát:

```text
+0x00 left
+0x08 parent
+0x10 right
+0x18 u32 KEY
+0x1C u32 real CLR metadata token
```

Ba node:

```text
node 0x24100668CE0: KEY 1 → 0x040088ED
node 0x24100668D10: KEY 2 → 0x010004C9
node 0x24100668D40: KEY 3 → 0x0A001D99
```

Cây có root KEY 2, left KEY 1, right KEY 3. Tại position này không có non-sentinel node KEY 4–6.

```text
CONFIRMED: đây là ordered KEY→real-token map cho transaction/lifetime quan sát.
STRONG: implementation tương thích MSVC std::_Tree/std::map<u32,u32>.
UNPROVEN: lifetime/scope toàn method và vị trí KEY 4–6.
```

### 14.9 CONFIRMED — exact map value → EAX → R14D data-flow

Read watchpoint trên `0x24100668CFC` bắt exact return load:

```asm
0x1800587F  mov eax,dword ptr [rbx+1Ch]
```

Với:

```text
RBX        = 0x24100668CE0
[RBX+18h]  = 1
[RBX+1Ch]  = 0x040088ED
```

Hàm epilogue rồi `ret` về:

```asm
0x180378851  mov r14d,eax
```

Do đó:

```text
CONFIRMED:
map[KEY 1].value = 0x040088ED
  → EAX = 0x040088ED
  → R14D = 0x040088ED
```

Đây là data dependency bằng instruction, không phải suy luận từ việc hai vùng nhớ lần lượt chứa các giá trị giống nhau.

### 14.10 CONFIRMED — exact R14D + resolver mask → masked-real R8D

Tại `AD7BF:30..32`:

```asm
0x180379287  mov r8d,dword ptr [rax+30h]
0x18037928B  xor r8d,r14d
0x18037928E  mov dword ptr [rsp+150h],r8d
```

Với:

```text
RAX          = resolver state 0x24100666840
[RAX+30h]    = 0x6A714B62
R14D         = 0x040088ED
R8D sau XOR  = 0x6E71C38F
```

Công thức exact:

```text
maskedReal = resolverMask XOR realToken
0x6E71C38F = 0x6A714B62 XOR 0x040088ED
```

```text
CONFIRMED: map real token → EAX → R14D → XOR mask → R8D masked-real.
CONFIRMED: stack slot [rsp+150] nhận masked-real token trước cache lookup.
```

Việc slot trước đó từng chứa raw virtual token chỉ là storage lifecycle; bằng chứng semantic nằm ở chain register/instruction trên. Từ `scrub` bị loại bỏ vì không có bằng chứng.

### 14.11 Pipeline runtime canonical cho KEY 1

```text
virtual token 0x04800001
    ↓ KEY extraction / lookup call context
ordered KEY→real-token map
    ↓ node KEY 1, value 0x040088ED
mov eax,[node+1Ch]
    ↓
EAX = 0x040088ED
    ↓ mov r14d,eax
R14D = real CLR token
    ↓ mov r8d,[resolverState+30h]
    ↓ xor r8d,r14d
R8D = masked-real 0x6E71C38F
    ↓ cache lookup 0x1800021B0, key (module, masked-real)

cache hit:
    node handles → output

cache miss:
    request.token ^= resolverMask
    ↓ real CLR token 0x040088ED
    ↓ underlying CoreCLR CEEInfo::resolveToken
    ↓ cache insert 0x1800058A0

runtime handles
    ↓ 0x38-byte copy into CORINFO_RESOLVED_TOKEN+0x18
```

KEY 2 independently xác nhận cùng mask:

```text
0x6B714FAB XOR 0x6A714B62 = 0x010004C9
```

Offline rebuilder không cần mô phỏng runtime handle cache. Cạnh bắt buộc là nguồn host/offline dựng:

```text
method/KEY → real CLR metadata token
```

### 14.12 CURRENT STATUS và ưu tiên mới

```text
CONFIRMED
  S0 record layout và CSV projection trên toàn bộ 10960 record
  nLocals == raw.itemCount; itemBytes == 4*itemCount
  recordSize = 12+itemBytes+ehDataSize; coverage đúng 0x57C08
  ilOffset recurrence đúng toàn bộ 10599? correction: 10959 cạnh
  ordered KEY→real-token map, node +18 KEY / +1C token
  KEY1→040088ED, KEY2→010004C9, KEY3→0A001D99
  map value→EAX→R14D exact data-flow
  R8D=[resolverState+30] XOR R14D exact data-flow
  handle cache key (module, masked-real), CoreCLR delegation và 0x38-byte output copy

STRONG
  payload dùng encoded/structured representation
  KEY scope cục bộ theo method/transaction
  resolver mask scope theo resolver/proxy instance

UNPROVEN
  exact source host/offline dựng KEY→real-token map
  KEY extraction từ virtual token ở lookup call-site
  KEY 4–6 và lifetime/map stage của suffix
  record của 0x060008E1 và RID→recordIndex
  payload/S2 representation và mask source offline
```

Thứ tự tiếp theo:

| Hạng | Việc | Mục tiêu |
|---|---|---|
| 1 | Truy caller/source dựng ordered KEY→real-token map | Đóng nguồn offline quan trọng nhất |
| 2 | Bắt KEY extraction và comparator/lookup input | Hoàn tất virtual token → KEY → node |
| 3 | Kiểm map ở các lifetime/position muộn để tìm KEY 4–6 | Hoàn thành method oracle |
| 4 | Đếm MethodDef rows host | Phân biệt dense protected index / permutation |
| 5 | Join method `0x060008E1` với S0 record | Cố định RID→record và field semantics |
| 6 | Reverse payload/S2 bằng cặp method/KEY đã biết | Tiến tới host-only decoder |

Ghi chú sửa typo canonical: số quan hệ `ilOffset` được kiểm là **10.959**, không phải `10.599`.
