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

---

## AK.19.2 — Full body `0x060008E1`, dynamic prefix và live token-resolution entry

### Dữ liệu thô: MethodBody đầy đủ

```text
ILCode     = 0x24100669E10
ILCodeSize = 0x2D

00 7F 01 00 80 04 FE 16 02 00 80 01 6F 03 00 80
0A 2D 01 00 2B 05 28 7D B9 39 5F 28 04 00 80 06
28 05 00 80 06 02 28 06 00 80 0A 00 2A
```

SOS:

```text
Method Name: UNKNOWN
mdToken:     0x060008E1
Module:      0x7FFA74E9E0A0
IsJitted:    no
```

### Decode IL theo operand boundaries

```text
IL_0000: 00                         nop
IL_0001: 7F 01 00 80 04             ldsflda      0x04800001
IL_0006: FE 16 02 00 80 01          constrained. 0x01800002
IL_000C: 6F 03 00 80 0A             callvirt     0x0A800003
IL_0011: 2D 01                      brtrue.s     IL_0014
IL_0013: 00                         nop
IL_0014: 2B 05                      br.s         IL_001B
IL_0016: 28 7D B9 39 5F             call         0x5F39B97D
IL_001B: 28 04 00 80 06             call         0x06800004
IL_0020: 28 05 00 80 06             call         0x06800005
IL_0025: 02                         ldarg.0
IL_0026: 28 06 00 80 0A             call         0x0A800006
IL_002B: 00                         nop
IL_002C: 2A                         ret
```

### Prefix identity

Hai mươi byte đầu của body:

```text
00 7F 01 00 80 04 FE 16 02 00 80 01 6F 03 00 80 0A 2D 01 00
```

khớp byte-for-byte buffer `0x24100669774`, size `0x14`, đã trace trong AK.2–AK.10.

### CONFIRMED

- Method handle `0x7FFA75B84208` ánh xạ tới managed method token `0x060008E1` trong module `0x7FFA74E9E0A0`.
- Full JIT input body dài `0x2D` byte.
- Prefix `[IL+0x00, IL+0x14)` trùng tuyệt đối với buffer 20 byte đã quan sát trước đó.
- Body đi vào `clrjit!CILJit::compileMethod` với ít nhất sáu metadata-token operands vẫn ở virtual format:

```text
0x04800001
0x01800002
0x0A800003
0x06800004
0x06800005
0x0A800006
```

- Do đó HVM proxy vẫn phải tham gia token resolution cho method `0x060008E1`; hook `compileMethod` đơn độc chưa cho CLR-valid MethodBody để patch thẳng.

### RETRACTED / sửa phạm vi

- Không tiếp tục xem buffer 20 byte là một MethodBody độc lập chỉ dựa trên việc nó kết thúc bằng `00`.
- Với method `0x060008E1`, nó là prefix thật của full body `0x2D` byte.
- Chưa gọi nó là template toàn corpus cho tới khi có thêm methods hoặc write/copy provenance.

### STRONG

- Kiến trúc hiện phù hợp với:

```text
dynamic/generated prefix 0x14
    + method-specific suffix 0x19
    -> full JIT body 0x2D
```

- Operand `0x5F39B97D` tại `IL_0016` là dead/junk candidate vì unconditional `br.s IL_001B` tại `IL_0014` bỏ qua instruction đó trên đường vào bình thường.

### UNPROVEN

- Metadata record của RID `0x8E1` có `codeSize = 0x19` hay không.
- Prefix 20 byte được copy nguyên khối, regenerate hay chỉ trùng nội dung.
- JIT có gọi resolver cho dead operand `0x5F39B97D` hay không.
- Method name vẫn chưa resolve được dù mdToken đã có.

## Live hit tại `HVMRun64+0x7850`

Từ entry `compileMethod` của `0x060008E1`:

```text
bc *
!tt 11A7E3:D6C
bp 0x180007850
bp 0x180043FFB
g
```

Breakpoint `0x180007850` hit trước compile return:

```text
Position = 11A7E4:8EC
RIP = 0x180007850
RCX = 0x2410066E720
RDX = 0x04800001
R8  = 0x7FFAD4EA841F
R9  = 0x4
RAX = 0x04000000
RSP = 0xFD8257AC88
```

Đáng chú ý:

```text
compileMethod ICorJitInfo* = 0x2410066E718
resolver RCX               = 0x2410066E720
                              = proxy + 8
```

Stack entry:

```text
[RSP+0x00] = 0x18003EEE7 ; exact return address
```

Entry code lưu arguments rồi nhảy vào obfuscated implementation:

```text
0x180007850 mov [rsp+18h],r8
0x180007855 mov [rsp+10h],edx
0x180007859 mov [rsp+8],rcx
...
0x18000787C mov edi,edx
0x18000787E mov rbx,rcx
0x180007881 lea rcx,[0x18005ECC0]
0x180007888 jmp 0x1803780C4
```

### CONFIRMED

- `0x180007850` được thực thi trên live compile path của method `0x060008E1`.
- Input `EDX` tại entry là chính virtual field token đầu tiên của body: `0x04800001`.
- Routine chạy trước return site `0x180043FFB`.
- Vì vậy `0x180007850` là token-resolution entry/helper thực sự cho virtual tokens trong build này.
- Nó không phải direct qword slot trong hai bảng đã search ở AK.17; caller có thể đi qua thunk/secondary interface dispatch.

### STRONG

- `RCX = proxy + 8` phù hợp với việc routine được gọi qua secondary interface subobject/vtable bắt đầu tại qword thứ hai `0x1800739A8`.
- `RAX = 0x04000000` phù hợp với target metadata table mask của expected mapping `0x04800001 -> 0x040088ED`, nhưng output mapping chưa được quan sát nên chưa nâng CONFIRMED.

### UNPROVEN

- Return value/output location chứa `0x040088ED`.
- ABI chính xác của helper và vai trò của `R8`, `R9`.
- Direct caller instruction/thunk dẫn tới `0x180007850`.
- Thứ tự và kết quả cho năm virtual tokens còn lại.

## Bước phân biệt tiếp theo

Từ vị trí hiện tại trong resolver, bắt exact return continuation:

```text
bp 0x18003EEE7
g
r rip,rax,rbx,rcx,rdx,rsi,rdi,r8,r9,r10,r11,rsp
u @rip L12
dq @rsp L10
```

Tìm expected real token ở stack và proxy state sau return:

```text
s -d @rsp L100 040088ed
s -d 0x2410066E718 L200 040088ed
```

Sau đó `g` để bắt lần kế tại `0x180007850` và ghi `RDX`; các input sống dự kiến từ IL là:

```text
0x01800002
0x0A800003
0x06800004
0x06800005
0x0A800006
```

Không mặc định dead operand `0x5F39B97D` sẽ được resolve.
