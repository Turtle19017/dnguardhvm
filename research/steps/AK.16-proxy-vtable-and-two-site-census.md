# BƯỚC AK.16 — Proxy vtable thật, full IL `[B]` và census hai JIT wrapper sites

Mẫu: `LordsMobileBot.exe`  
Ngày: 2026-08-06

## Câu hỏi

1. `RDX` tại call `[B]` có thật sự là object `ICorJitInfo` do HVM sở hữu không?
2. Hai dự đoán cũ `[@rdx]=0x180035100` và `[vtable+0xE0]=0x180007850` có đúng không?
3. Full body `[B]` chứa token thật và native/runtime constants nào?
4. Bao nhiêu `compileMethod` calls đi qua từng return site trong HVM image?

## Dữ liệu thô

Tại `ABBAE:FDD`:

```text
RDX = 0x24100668928 ; ICorJitInfo*
R8  = 0xFD8257D600  ; CORINFO_METHOD_INFO*
```

Object header:

```text
[0x24100668928+0x00] = 0x1800739E8
[0x24100668928+0x08] = 0x1800739A8
```

Cả hai qword đều trỏ vào image `HVMRun64`.

Slot đã thử:

```text
[0x1800739E8 + 0xE0] = 0x18003EE80
```

không phải `0x180007850`.

Full IL body `[B]`, size `0x2C`:

```text
17 0A 21 02 DE 17 6A 41 02 00 00 73 9C 1D 00 0A
80 ED 88 00 04 21 E0 93 00 80 01 00 00 00 80 EE
88 00 04 17 0A DE 03 26 DE 00 06 2A
```

Census:

```text
all compileMethod calls = 0xCA3 = 3235
site 0x180043FFB      = 0x52  = 82
site 0x18003FB4F      = 0xC47 = 3143
all HVM-image returns = 0xC99 = 3225
```

Arithmetic:

```text
0x52 + 0xC47 = 0xC99
0xCA3 - 0xC99 = 0x0A
```

## Phân tích proxy

### CONFIRMED

- `RDX` được truyền vào `clrjit!CILJit::compileMethod` với vai trò `ICorJitInfo*`.
- Qword đầu object trỏ vào `0x1800739E8`, nằm trong image `HVMRun64`.
- Qword thứ hai cũng trỏ vào image `HVMRun64`.
- Vì vậy call `[B]` nhận một HVM-owned `ICorJitInfo` implementation/proxy object, không phải chỉ một pointer ngẫu nhiên trong HVM heap.

### RETRACTED

Hai identity cụ thể đặt trước trong AK.15A là sai:

```text
[@rdx]      != 0x180035100
[vtable+E0] != 0x180007850
```

Giá trị thật:

```text
[@rdx]      = 0x1800739E8
[vtable+E0] = 0x18003EE80
```

Không được gọi slot `+0xE0` là `resolveToken` trong build này.

### UNPROVEN

- Offset vtable thật của `resolveToken`.
- `0x180007850` có thuộc vtable phụ `0x1800739A8`, vtable chính ở offset khác, hay là resolver ở một layer khác.

## Decode full IL `[B]`

```text
00: 17                            ldc.i4.1
01: 0A                            stloc.0
02: 21 02 DE 17 6A 41 02 00 00    ldc.i8 0x000002416A17DE02
0B: 73 9C 1D 00 0A                newobj 0x0A001D9C
10: 80 ED 88 00 04                stsfld 0x040088ED
15: 21 E0 93 00 80 01 00 00 00    ldc.i8 0x00000001800093E0
1E: 80 EE 88 00 04                stsfld 0x040088EE
23: 17                            ldc.i4.1
24: 0A                            stloc.0
25: DE 03                         leave.s +3
27: 26                            pop
28: DE 00                         leave.s +0
2A: 06                            ldloc.0
2B: 2A                            ret
```

### CONFIRMED

Call `[B]` đi vào clrjit với ít nhất ba CLR metadata tokens thật tại đúng operand boundaries:

```text
0x0A001D9C
0x040088ED
0x040088EE
```

`0x040088ED` khớp field thật đã map từ virtual token `0x04800001`.

Body còn chứa absolute 64-bit constant:

```text
0x00000001800093E0
```

nằm trong image `HVMRun64` của trace hiện tại.

### STRONG

- `[B]` là một HVM-generated/bootstrap body đã được token-resolve trước `compileMethod`.
- Verbatim lifting body này sang output offline sẽ không độc lập khỏi HVM, vì nó chứa absolute address vào `HVMRun64` trong trace hiện tại.

### UNPROVEN

- `0x000002416A17DE02` có phải pointer sống hay encoded/cookie value.
- Mọi HVM-generated methods đều có token thật trước JIT.
- Mọi methods đều chứa absolute native pointers.
- `[B]` có phải user virtualized method hay injected/bootstrap method.

## Census return sites

### CONFIRMED

Trong trace này:

```text
82 calls   return qua 0x180043FFB
3143 calls return qua 0x18003FB4F
3225 calls có return address trong HVM image
10 calls   có return address ngoài HVM image
```

Vì:

```text
82 + 3143 = 3225
```

nên mọi HVM-origin `compileMethod` call trong trace thuộc đúng một trong hai return sites trên.

### STRONG

Ba mẫu hiện có vẫn tương thích với classifier:

```text
0x180043FFB -> ILCode trong HVM heap/arena
0x18003FB4F -> ILCode trong PE image
```

### UNPROVEN

- Tất cả 82 site43 calls đều là protected user methods.
- Tất cả 3143 site3f calls đều là ordinary unprotected methods.
- 82 calls là 82 unique methods; tiering/recompile có thể lặp method handle.

Nếu classifier được xác nhận trên mẫu phân bố rộng, runtime coverage upper bound của candidate protected compile events là:

```text
82 / 10960 = 0.748%
```

Unique-method coverage sẽ nhỏ hơn hoặc bằng con số này.

## Bước tiếp theo — AK.17

### A. Tìm resolver slot thật trong hai vtables

```text
s -q 0x1800739A8 L400 0x180007850
s -q 0x1800739E8 L400 0x180007850
```

Nếu có match, lấy offset:

```text
? <MATCH_ADDRESS> - 0x1800739A8
? <MATCH_ADDRESS> - 0x1800739E8
```

Không có match thì không tiếp tục gọi `0x180007850` là direct vtable slot.

### B. Lấy năm site43 samples phân bố theo trace

Trước tiên in các entry rải đều:

```text
dx -g @$site43.Take(2)
dx -g @$site43.Skip(0x10).Take(1)
dx -g @$site43.Skip(0x20).Take(1)
dx -g @$site43.Skip(0x30).Take(1)
dx -g @$site43.Skip(0x40).Take(1)
dx -g @$site43.Skip(0x50).Take(2)
```

Tại mỗi `TimeStart`:

```text
!tt <TIMESTART>
r rdx,r8,r9
dq @r8 L4
dq @r8+10 L1
dd @r8+18 L1
```

Đọc đúng `ILCodeSize`, parse opcode boundaries rồi ghi:

```text
method handle
ILCode pointer/size
ILCode region
metadata-token operands
ldc.i8 constants
RDX proxy pointer/vtable
```

Mục tiêu là xác nhận hoặc bác classifier site43 trên nhiều thời điểm và xem token thật có nhất quán ngoài bootstrap body `[B]` hay không.
