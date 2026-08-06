# BƯỚC AK.14 — Call `compileMethod` kế tiếp không nhận trực tiếp stub 20 byte

Mẫu: `LordsMobileBot.exe`  
Ngày: 2026-08-06

## Câu hỏi

1. Call `compileMethod` đầu tiên sau emit window `A9BD9:0 -> A9BDE:0`, tại `ABBAE:FDD`, có nhận trực tiếp buffer `0x24100669774`, size `0x14` không?
2. Hai call kế tiếp có dùng IL pointer/size khác nhau không?
3. Association đặt trước trong AK.13 có đứng không?

## Call `[0xB]` tại `ABBAE:FDD`

Windows x64 entry registers:

```text
RCX = 0x7FFAD4FB0DE0
RDX = 0x24100668928
R8  = 0xFD8257D600   ; CORINFO_METHOD_INFO*
R9  = 0xFFFFFFFF
```

Dump `CORINFO_METHOD_INFO`:

```text
[R8+0x00] = 0x7FFA74FC42E0
[R8+0x08] = 0x7FFA74E9E0A0
[R8+0x10] = 0x24100668754   ; ILCode
[R8+0x18] = 0x0000002C     ; ILCodeSize
[R8+0x1C] = 0x00000008
[R8+0x20] = 0x00000001
[R8+0x28] = 0x00000003
```

Hai tiêu chí đặt trước không khớp:

```text
expected ILCode     = 0x24100669774
actual ILCode       = 0x24100668754

expected ILCodeSize = 0x14
actual ILCodeSize   = 0x2C
```

20 byte đầu của method này:

```text
17 0A 21 02 DE 17 6A 41 02 00 00 73 9C 1D 00 0A 80 ED 88 00
```

Đây không phải stub:

```text
00 7F 01 00 80 04 FE 16 02 00 80 01 6F 03 00 80 0A 2D 01 00
```

## Call `[0xC]` tại `ABBFC:7C4`

```text
R8 = 0xFD8257D670

ILCode     = 0x241011A54B9
ILCodeSize = 0x12
method     = 0x7FFA74FC3C00
scope      = 0x7FFA74E9E0A0
```

## Call `[0xD]` tại `ABC54:16BB`

```text
R8 = 0xFD8257D640

ILCode     = 0x2410066A410
ILCodeSize = 0x31
method     = 0x7FFA750F3A60
scope      = 0x7FFA74E9E0A0
```

Ba call liên tiếp có ba IL pointer và ba size khác nhau:

```text
[B] 0x24100668754 / 0x2C
[C] 0x241011A54B9 / 0x12
[D] 0x2410066A410 / 0x31
```

## Kết luận

### CONFIRMED

- Call `[0xB]` không nhận trực tiếp buffer stub `0x24100669774`.
- Call `[0xB]` không có `ILCodeSize = 0x14`; size thực là `0x2C`.
- Bytes đầu của call `[0xB]` không khớp stub 20 byte.
- Calls `[0xB]`, `[0xC]`, `[0xD]` compile ba MethodBody khác nhau theo IL pointer/size.

### RETRACTED

- Association đặt trước trong AK.13:

```text
A9BD9..A9BDE emit stub -> call [0xB] tại ABBAE:FDD
```

không đứng ở dạng direct `CORINFO_METHOD_INFO.ILCode == emitted buffer`.

### STRONG

- Buffer `0x24100669774` là staging/intermediate artifact, hoặc được copy/transform trước khi tới `compileMethod`, hoặc emit transaction đó không liên quan trực tiếp tới call `[0xB]`.

### UNPROVEN

- Stub buffer được consume bởi component nào.
- Stub buffer có được copy sang một IL buffer khác trước JIT không.
- Call nào trong 3235 calls nhận nội dung tương đương stub.
- Calls `[B]`, `[C]`, `[D]` tương ứng method managed nào.

## Bước tiếp theo — AK.15

Truy memory consumer của buffer `0x24100669774..0x24100669788` thay vì đoán theo call chronology.

### 1. Kiểm tra nội dung buffer tại entry call `[B]`

```text
!tt ABBAE:FDD
db 0x24100669774 L14
```

### 2. Query reads/writes từ sau emit đến entry call `[B]`

```text
dx @$pe = @$create("Debugger.Models.TTD.Position",0xA9BDD,0x184E)
dx @$pb = @$create("Debugger.Models.TTD.Position",0xABBAE,0xFDD)
dx @$rStubPreB = @$cursession.TTD.MemoryForPositionRange(0x24100669774,0x24100669788,"r",@$pe,@$pb)
dx @$rStubPreB.Count()
dx -g @$rStubPreB
dx @$wStubPreB = @$cursession.TTD.MemoryForPositionRange(0x24100669774,0x24100669788,"w",@$pe,@$pb)
dx @$wStubPreB.Count()
dx -g @$wStubPreB
```

Không seek nếu query rỗng.

### 3. Query accesses trong chính call `[B]`

```text
dx @$pbe = @$create("Debugger.Models.TTD.Position",0xABBC2,0xAB6)
dx @$rStubB = @$cursession.TTD.MemoryForPositionRange(0x24100669774,0x24100669788,"r",@$pb,@$pbe)
dx @$rStubB.Count()
dx -g @$rStubB
dx @$wStubB = @$cursession.TTD.MemoryForPositionRange(0x24100669774,0x24100669788,"w",@$pb,@$pbe)
dx @$wStubB.Count()
dx -g @$wStubB
```

### 4. Map method handles nếu SOS có sẵn

```text
!dumpmd 0x7FFA74FC42E0
!dumpmd 0x7FFA74FC3C00
!dumpmd 0x7FFA750F3A60
```

Nếu SOS không nhận `!dumpmd`, không suy tên method từ pointer.

---

## Ghi chú sửa sau peer review

### `ICorJitInfo*` là ứng viên proxy HVM

Tại entry `[B]`:

```text
RDX = 0x24100668928 ; ICorJitInfo*
R8  = 0xFD8257D600  ; CORINFO_METHOD_INFO* trên stack
```

`RDX` nằm trong cùng vùng heap đã quan sát chứa nhiều state/arena của HVM. Đây là **STRONG** cho mô hình DNGuard truyền một object proxy `ICorJitInfo` riêng, nhưng chưa nâng CONFIRMED chỉ từ locality.

Kiểm chứng quyết định:

```text
!tt ABBAE:FDD
dq @rdx L1
dq poi(@rdx)+e0 L1
```

Dự đoán đặt trước:

```text
[@rdx]       = 0x180035100 ; proxy vtable đã ghi trước
[vtable+E0]  = 0x180007850 ; resolver slot đã biết
```

Nếu cả hai khớp, identity của proxy được nâng CONFIRMED. `CORINFO_METHOD_INFO` cung cấp IL/locals; proxy `ICorJitInfo` vẫn cần cho `resolveToken`, `getEHinfo` và các callback JIT khác.

### Decode IL `[B]`: một token thật đã CONFIRMED, token thứ hai còn thiếu một byte

20 byte đã dump decode theo boundary IL:

```text
17                            ldc.i4.1
0A                            stloc.0
21 02 DE 17 6A 41 02 00 00    ldc.i8 0x000002416A17DE02
73 9C 1D 00 0A                newobj 0x0A001D9C
80 ED 88 00 ??                stsfld operand chưa đủ 4 byte
```

Do đó:

### CONFIRMED

- JIT input `[B]` chứa ít nhất một metadata token thật: `0x0A001D9C`.
- Token này khớp pool entry `rid=0x1D9C, kind=5 -> MemberRef 0x0A001D9C`.

### STRONG

- Nếu byte kế tại `IL+0x14` là `04`, operand `stsfld` là `0x040088ED`, khớp field thật đã map từ virtual token `0x04800001`.

### UNPROVEN

- Mọi token trong HVM-generated methods đều đã là CLR token thật trước `compileMethod`.
- Một hook duy nhất tại `compileMethod` đủ cho toàn bộ corpus.

Cần dump đủ `0x2C` byte và parse operand theo opcode; không quét mọi cửa sổ 4 byte rồi kiểm bit `0x00800000` vì sẽ tạo false positive trên immediate/branch data.

### `ldc.i8` chưa được gọi là pointer runtime

Giá trị:

```text
0x000002416A17DE02
```

chỉ cùng prefix rộng `0x241`, nhưng không nằm trong hai interval đã biết:

```text
HVM arena gần 0x2410066xxxx
PE mapping gần 0x2410118xxxx
```

Nó có thể là pointer, cookie hoặc encoded state. Phải kiểm bằng `!address`, `dq` hoặc memory query trước khi kết luận method nhúng địa chỉ phiên chạy.

### Phân loại theo vùng `ILCode`

Ba mẫu tạo giả thuyết mạnh:

```text
[B] 0x24100668754 / 0x2C / return 0x180043FFB ; heap/arena candidate
[C] 0x241011A54B9 / 0x12 / return 0x18003FB4F ; PE image candidate
[D] 0x2410066A410 / 0x31 / return 0x180043FFB ; heap/arena candidate
```

### STRONG

- `ILCode` trong HVM heap/arena tương quan với return site `0x180043FFB`.
- `ILCode` trong PE image tương quan với `0x18003FB4F`.

### UNPROVEN

- Hai return sites phân loại hoàn hảo toàn bộ 3235 calls.
- Mọi arena ILCode là method bảo vệ và mọi PE ILCode là method thường.

Ưu tiên AK.15 được đổi từ truy riêng consumer của stub sang đóng proxy, phân loại call và lấy mẫu token thực. Truy consumer của stub vẫn là backlog, không còn là đường chính.