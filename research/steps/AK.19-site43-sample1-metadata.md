# BƯỚC AK.19 — Site43 sample đầu tiên ngoài bootstrap: arena IL và proxy class lặp lại

Mẫu: `LordsMobileBot.exe`  
Ngày: 2026-08-06

## Câu hỏi

1. Sample tại `11A7E3:D6C` có thật sự thuộc return site `0x180043FFB` đã enumerate không?
2. `ILCode` của sample có tiếp tục nằm trong HVM heap/arena không?
3. `ICorJitInfo*` của sample có dùng cùng hai HVM vtable pointers như call `[B]` không?
4. Sample này đã đủ để kết luận token thật hay chưa?

## Dữ liệu thô

Seek:

```text
!tt 11A7E3:D6C
```

Entry:

```text
clrjit!CILJit::compileMethod
RDX = 0x2410066E718
R8  = 0xFD8257D160
R9  = 0xFFFFFFFF
```

`CORINFO_METHOD_INFO`:

```text
[R8+0x00] = 0x7FFA75B84208 ; method handle
[R8+0x08] = 0x7FFA74E9E0A0 ; scope
[R8+0x10] = 0x24100669E10  ; ILCode
[R8+0x18] = 0x0000002D     ; ILCodeSize
[R8+0x1C] = 0x00000008     ; maxStack candidate
```

Proxy object header:

```text
[RDX+0x00] = 0x1800739E8
[RDX+0x08] = 0x1800739A8
```

## Phân tích

### CONFIRMED

- `11A7E3:D6C` là một `clrjit!CILJit::compileMethod` entry đã enumerate từ tập site43.
- `ILCode = 0x24100669E10`, size `0x2D`.
- `ILCode` nằm trong cùng vùng `0x2410066xxxx` đã quan sát chứa HVM-generated/staged bodies ở samples `[B]` và `[D]`.
- `RDX` là object khác với call `[B]`:

```text
[B]       RDX = 0x24100668928
AK.19 S1  RDX = 0x2410066E718
```

- Hai object khác nhau có cùng hai qword đầu:

```text
0x1800739E8
0x1800739A8
```

- Vì vậy ít nhất hai `ICorJitInfo` proxy instances khác nhau dùng cùng HVM-owned interface/vtable pair.

### STRONG

- Classifier:

```text
return site 0x180043FFB -> ILCode trong HVM heap/arena
```

đã được xác nhận trên ba samples `[B]`, `[D]` và AK.19 sample 1, trong đó sample 1 nằm muộn hơn nhiều trong trace.

- HVM có vẻ tạo hoặc tái sử dụng nhiều proxy object instances cùng implementation class, thay vì một singleton pointer cố định.

### UNPROVEN

- Byte IL của sample chưa được dump, nên chưa biết:
  - metadata-token operands là token thật hay virtual token;
  - body có nhúng native/runtime absolute addresses hay không;
  - method này là user virtualized method, bootstrap method hay helper generated khác.
- Chưa map được method handle `0x7FFA75B84208` sang managed token/name.
- Chưa biết lifetime/ownership chính xác của proxy object `0x2410066E718`.

## Bước tiếp theo — dump đúng `0x2D` byte

Tại vị trí hiện tại:

```text
db 0x24100669E10 L2D
```

Hoặc theo structure pointer:

```text
db poi(@r8+10) L2D
```

Sau khi có bytes:

1. Parse IL theo opcode boundaries.
2. Chỉ phân loại các operand thật sự là metadata token.
3. Ghi mọi `ldc.i8`, `ldftn`, `calli`, native pointer candidate hoặc absolute HVM address.
4. So sánh token operands với token pool host đã giải.

Định danh method nếu SOS hoạt động:

```text
!dumpmd 0x7FFA75B84208
```

Không suy tên/RID từ method-handle pointer nếu SOS không trả kết quả.
