# BƯỚC AK.18 — Khởi tạo lại `site43` thành công và lấy được các `TimeStart` phân bố rộng

Mẫu: `LordsMobileBot.exe`  
Ngày: 2026-08-06

## Câu hỏi

1. Có khôi phục lại được census `compileMethod` và tập call tại return site `0x180043FFB` không?
2. Có lấy được các `TimeStart` thật phân bố rộng trong trace không?
3. Dump tại `ABC54:16BB` có phải sample mới hay chỉ xác nhận lại sample `[D]` đã biết?

## Dữ liệu thô

Khởi tạo lại:

```text
dx @$jit17 = @$cursession.TTD.Calls("clrjit!CILJit::compileMethod")
dx @$jit17.Count()
dx @$site43_17 = @$jit17.Where(c => c.ReturnAddress == 0x180043FFB)
dx @$site43_17.Count()
```

Kết quả:

```text
@$jit17.Count()     = 0xCA3
@$site43_17.Count() = 0x52
```

Các vị trí phân bố rộng đã enumerate:

```text
[0xB]   ABBAE:FDD
[0xD]   ABC54:16BB
[0x6EB] 11A7E3:D6C
[0x70F] 121937:1754
[0x74F] 129170:768
[0x932] 144D1C:1E0A
[0xA20] 162182:14E4
[0xA26] 164BB7:8A2
```

Tại `ABC54:16BB`:

```text
RDX = 0x24100668648
R8  = 0xFD8257D640
R9  = 0xFFFFFFFF

method      = 0x7FFA750F3A60
scope       = 0x7FFA74E9E0A0
ILCode      = 0x2410066A410
ILCodeSize  = 0x31
```

## Phân tích

### CONFIRMED

- TTD call census được khởi tạo lại thành công.
- Tổng call vẫn là `0xCA3 = 3235`.
- Return site `0x180043FFB` vẫn có `0x52 = 82` call.
- Đã có sáu `TimeStart` mới phân bố rộng ngoài hai call đầu:

```text
11A7E3:D6C
121937:1754
129170:768
144D1C:1E0A
162182:14E4
164BB7:8A2
```

- Seek tới `ABC54:16BB` thành công.
- Dump tại đó khớp đúng sample `[D]` đã biết: `ILCode=0x2410066A410`, `ILCodeSize=0x31`.

### Không phải bằng chứng mới

- `ABC54:16BB` không phải sample mới; nó chỉ tái xác nhận sample `[D]` từ AK.14.
- `dx -r1 -g @$site43_17.Take(2).OrderBy(ThreadId)` không bổ sung thông tin vì hai row đầu cùng thread.
- `dx -r1 @$jit17` chỉ liệt kê collection, không phân loại thêm call.

### UNPROVEN

- `ILCode` và token operands của sáu vị trí phân bố rộng chưa được dump.
- Classifier `site43 -> HVM arena/generated IL` chưa được nâng ngoài các mẫu `[B]` và `[D]`.
- Chưa biết sáu call mới có token thật, virtual token, native pointer hay dynamic constants.

## Bước tiếp theo — AK.19

Inspect từng `TimeStart` mới. Không dùng placeholder.

### Sample 1

```text
!tt 11A7E3:D6C
r rdx,r8,r9
dq @r8 L4
dq @r8+10 L1
dd @r8+18 L1
```

### Sample 2

```text
!tt 121937:1754
r rdx,r8,r9
dq @r8 L4
dq @r8+10 L1
dd @r8+18 L1
```

### Sample 3

```text
!tt 129170:768
r rdx,r8,r9
dq @r8 L4
dq @r8+10 L1
dd @r8+18 L1
```

### Sample 4

```text
!tt 144D1C:1E0A
r rdx,r8,r9
dq @r8 L4
dq @r8+10 L1
dd @r8+18 L1
```

### Sample 5

```text
!tt 162182:14E4
r rdx,r8,r9
dq @r8 L4
dq @r8+10 L1
dd @r8+18 L1
```

### Sample 6

```text
!tt 164BB7:8A2
r rdx,r8,r9
dq @r8 L4
dq @r8+10 L1
dd @r8+18 L1
```

Sau mỗi sample, chỉ dump IL khi pointer và size hợp lệ. Dùng size thật thay cho placeholder:

```text
db poi(@r8+10) L<SIZE_HEX_THẬT>
```

Không cần chạy lại census giữa các sample.