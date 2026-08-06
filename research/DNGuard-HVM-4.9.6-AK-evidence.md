# DNGuard HVM 4.9.6 — Evidence Log từ BƯỚC AK

Mẫu đang phân tích: `LordsMobileBot.exe`

Mục tiêu của file này là ghi lại **chỉ các bước bắt đầu từ AK trở đi**, theo dạng append-only. Không nhập lại handoff cũ từ Notion để tránh trộn kết luận cũ, kết luận đã rút lại và bằng chứng mới.

## Quy ước mức độ

- **CONFIRMED**: có dataflow, disassembly, validator khép kín hoặc số học trực tiếp.
- **STRONG**: nhiều chứng cứ độc lập cùng hướng nhưng còn mô hình thay thế hợp lý.
- **UNPROVEN**: giả thuyết làm việc, chưa có phép thử phân biệt.
- **RETRACTED**: đã bị dữ liệu mới hoặc lỗi phương pháp bác bỏ.

## Khuôn ghi mỗi bước

Mỗi bước mới phải có:

1. Câu hỏi.
2. Giả thuyết đặt trước.
3. Lệnh hoặc script.
4. Đối chứng dương.
5. Dữ liệu thô.
6. Phân tích.
7. Kết luận theo mức bằng chứng.
8. Artifact sinh ra.
9. Bước phân biệt tiếp theo.

---

# BƯỚC AK.1 — Kiểm tra parser payload và continuity của reverse stream

## 1. Câu hỏi

1. Header payload có thật sự là:

   ```c
   [u8 kind][u24 len][u32 s2off][body align8(len)]
   ```

   hay không?

2. Dải static stream quanh `HVMRun64+0x7CC5` có tiếp tục liền mạch trước và sau transaction tạo opcode `0x7F` hay không?

## 2. Giả thuyết đặt trước

- **H1:** scanner cũ bỏ các entry có `len >= 0x100`; sẽ xuất hiện candidate `kind=8` có byte cao của `u24 len` khác 0.
- **H2:** tồn tại một suffix chain `kind=8` kết thúc đúng EOF theo công thức:

  ```text
  next = p + 8 + align8(u24_len)
  ```

- **H3:** các lần đọc trước `0x180007CCE` và sau `0x180007CC5` tiếp tục phủ một dải địa chỉ giảm dần.

## 3. AK-OFFLINE — dữ liệu thô

```text
=== HEADER CANDIDATES ===
all_valid_header    : 7092
len_lt_100          : 7092
len_lt_10000        : 7092

Ví dụ len >= 0x100:
<không có>

=== EXACT KIND-8 SUFFIX CHAINS ===
số vị trí có thể bắt đầu suffix: 0

RuntimeError: Không có chuỗi kind=8 nào kết thúc đúng EOF.
Mô hình u24 len + align8 không đúng ở cuối payload.
```

## 4. Phân tích AK-OFFLINE

- Toàn bộ `7.092` candidate `kind=8` hợp lệ đều có giá trị trường quan sát được nhỏ hơn `0x100`.
- Không có population `kind=8` nào với byte cao tại `p+2` hoặc `p+3` khác 0.
- Vì vậy bộ lọc cũ không hề bỏ một population `kind=8` có `u24 len >= 0x100`.
- Không tồn tại suffix chain kết thúc đúng EOF theo luật `next = p + 8 + align8(u24)`.
- Ba entry đầu khớp luật length chỉ là bằng chứng cục bộ, không thể tổng quát hóa cho toàn blob.

## 5. Kết luận AK-OFFLINE

### RETRACTED

- `b1` chắc chắn là byte thấp của `u24 len`.
- Scanner cũ bỏ gần 4.000 method chỉ vì lọc `len < 256`.
- `next = p + 8 + align8(len)` là grammar tổng quát của payload.

### CONFIRMED

- Population candidate trên lưới 8 vẫn là `7.092` entry `kind=8` theo validator hiện tại.

### UNPROVEN

- `b1` có thể là độ dài 8-bit, opcode, class ID hoặc field khác.
- Candidate `kind=8/3` có phải top-level method directory hay không.
- Quan hệ `10.960 S0 record` với candidate payload.

### Dữ liệu không được dùng để suy body boundary

- `pl_dir.csv`
- `pl_dir2.csv`
- `dir_full.csv`
- Mọi boundary sinh trực tiếp từ giả thuyết `u24 len`.

---

## 6. AK-RUNTIME — dữ liệu thô

### Read ngay trước đoạn đã biết

```text
A9BDC:1815  addr 0x180007CD3  size 4  -> CD3..CD6
A9BDC:183D  addr 0x180007CD2  size 1  -> CD2
```

### Read trong đoạn đã biết

```text
A9BDC:1860  addr 0x180007CCE  size 4  -> CCE..CD1
A9BDC:188D  addr 0x180007CCA  size 4  -> CCA..CCD
A9BDC:18B7  addr 0x180007CC6  size 4  -> CC6..CC9
A9BDC:18CC  addr 0x180007CC5  size 1  -> CC5
```

### Read ngay sau đoạn đã biết

```text
A9BDC:18F9  addr 0x180007CC4  size 1  -> CC4
A9BDC:1918  addr 0x180007CC0  size 4  -> CC0..CC3
A9BDC:1947  addr 0x180007CB8  size 8  -> CB8..CBF
A9BDC:1960  addr 0x180007CB4  size 4  -> CB4..CB7
A9BDC:1990  addr 0x180007CB0  size 4  -> CB0..CB3
A9BDC:19D2  addr 0x180007CAC  size 4  -> CAC..CAF
A9BDC:1A04  addr 0x180007CA8  size 4  -> CA8..CAB
A9BDC:1A1E  addr 0x180007CA7  size 1  -> CA7
```

## 7. Invariant runtime

Chronology tạo union chính xác:

```text
[0x180007CA7, 0x180007CD7)
length          = 0x30 = 48 byte
sum(read sizes) = 48 byte
gap             = 0
overlap         = 0
direction       = strictly descending
```

## 8. Kết luận AK-RUNTIME

### CONFIRMED

- Một static encoded stream dài ít nhất `48` byte được tiêu thụ liên tục theo địa chỉ giảm từ `CD6` xuống `CA7`.
- Đây không phải các constant rời rạc tình cờ nằm cạnh nhau.
- Trong bốn read giữa, effective source address rút gọn chính xác thành `[RBX]`.

### STRONG

- Transaction hiện tại đang chạy một reverse-consumed microprogram hoặc bytecode stream.

### UNPROVEN

- RBX vẫn là cursor tại toàn bộ các event mở rộng `1815`, `183D`, `18F9` ... `1A1E`.
- RBX là program counter toàn cục xuyên toàn VM.
- Stream này được dùng chung cho mọi method HVM hay chỉ cho stub cctor đang trace.

## 9. Hệ quả chiến lược

- Runtime hiện là hướng có bằng chứng mạnh nhất.
- Offline payload header phải quay lại từ đầu bằng successor graph hoặc parser runtime, không tiếp tục ép mô hình `u24 len`.
- Nhiệm vụ kế tiếp là ánh xạ từng interval static stream sang các write vào IL buffer.

## 10. Bước tiếp theo

### AK.2-RUNTIME

1. Lấy toàn bộ read static stream trong cửa sổ `A9BDC:1800 -> A9BDC:1A40`.
2. Lấy toàn bộ write vào IL buffer trong cùng cửa sổ.
3. Chia stream read thành các đoạn giữa hai write IL liên tiếp.
4. Dựng bảng:

   ```text
   static interval -> handler IP -> emitted IL byte/word
   ```

5. Chụp register/disassembly tại các event đại diện để xác nhận RBX tiếp tục là source cursor.

---

_End of current AK evidence log._
