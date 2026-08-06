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
