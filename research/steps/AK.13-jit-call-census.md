# BƯỚC AK.13 — Census `compileMethod` và liên hệ với emit window

Mẫu: `LordsMobileBot.exe`  
Ngày: 2026-08-06

## Câu hỏi

1. Trace có bao nhiêu invocation thật của `clrjit!CILJit::compileMethod`?
2. Current HVM emit window `A9BD9:0 -> A9BDE:0` nằm ở đâu so với các JIT call?
3. Aggregate static-page census có khớp một interval dài `0x444` không?

## Aggregate static-page endpoint

Event có start address lớn nhất:

```text
Position = A9BDB:53F
Address  = 0x180007FFF
Size     = 0x4
IP       = 0x180202BE7
```

Do đó:

```text
staticEnd = 0x180007FFF + 4
          = 0x180008003
```

Với:

```text
Min start = 0x180007BBF
Sum(Size) = 0x444
```

thì:

```text
staticEnd - Min = 0x180008003 - 0x180007BBF
                = 0x444
                = Sum(Size)
```

### CONFIRMED

- Aggregate span và tổng kích thước read khớp chính xác `0x444` byte.
- Read cuối bắt đầu tại cuối page `0x180007FFF` và vượt page boundary đến `0x180008003`.

### Không được suy

- Equality trên chỉ là aggregate invariant.
- Nó chưa tự chứng minh pairwise gap-free/overlap-free cho toàn bộ 339 event; overlap và gap có thể bù nhau về tổng độ dài.

## Census `compileMethod`

Query:

```text
dx @$jitcalls = @$cursession.TTD.Calls("clrjit!CILJit::compileMethod")
dx @$jitcalls.Count()
```

Kết quả:

```text
@$jitcalls.Count() = 0xCA3 = 3235
```

Query symbol hoạt động và trả hàng nghìn call, không phải một call duy nhất.

Các call hiển thị đầu tiên gồm:

```text
[0x0]  398A:B65   -> 3A25:96C
[0x1]  3A27:190D  -> 3A41:5E4
...
[0xA]  959E:88    -> 9964:375   ReturnAddress=0x18003FB4F
[0xB]  ABBAE:FDD  -> ABBC2:AB6  ReturnAddress=0x180043FFB
[0xC]  ABBFC:7C4  -> ABC4F:1165 ReturnAddress=0x18003FB4F
[0xD]  ABC54:16BB -> AD97D:AC   ReturnAddress=0x180043FFB
```

Nhiều call có return address nằm trong image HVMRun64, đặc biệt hai site lặp:

```text
0x18003FB4F
0x180043FFB
```

## Liên hệ với current emit window

Current emit transaction:

```text
A9BD9:0 -> A9BDE:0
```

Call HVM-origin hiển thị trước đó:

```text
[0xA] 959E:88 -> 9964:375
```

Call HVM-origin hiển thị kế tiếp:

```text
[0xB] ABBAE:FDD -> ABBC2:AB6
```

Theo thứ tự TTD position:

```text
9964:375 < A9BD9:0 < A9BDE:0 < ABBAE:FDD
```

### CONFIRMED

- Current emit window không nằm bên trong call `[0xA]` hoặc `[0xB]`.
- Nó diễn ra sau khi call `[0xA]` đã return và trước entry của call `[0xB]`.
- Trace chứa 3235 JIT compile calls; giả thuyết “trace chỉ JIT một method” bị bác bỏ hoàn toàn.
- Nhiều call nhìn thấy được gọi từ HVMRun64 wrapper sites.

### STRONG

- Call `[0xB]` là candidate trực tiếp tiêu thụ MethodBody vừa được HVM dựng trong window `A9BD9..A9BDE`.
- Kiến trúc phù hợp với:

```text
HVM chuẩn bị / phát MethodBody
    -> gọi clrjit!CILJit::compileMethod
```

thay vì HVM emit diễn ra bên trong clrjit.

### UNPROVEN

- `CORINFO_METHOD_INFO.ILCode` của call `[0xB]` có đúng bằng `0x24100669774` hay không.
- `ILCodeSize` của call `[0xB]` có đúng `0x14` hay không.
- Call `[0xB]` là injected cctor stub hay method khác.
- Bao nhiêu trong 3235 call thực sự đi qua HVM wrapper.
- Trace có bao nhiêu user virtualized methods khác nhau.

## Bước tiếp theo — AK.14

Kiểm tra trực tiếp `CORINFO_METHOD_INFO*` của call `[0xB]` tại entry `ABBAE:FDD`.

Windows x64 ABI cho signature:

```text
RCX = this
RDX = ICorJitInfo*
R8  = CORINFO_METHOD_INFO*
R9D = flags
```

Chạy:

```text
!tt ABBAE:FDD
r rcx,rdx,r8,r9,rsp
u @rip L3
dq @r8 L8
dd @r8+18 L6
```

Chỉ khi qword tại `[R8+0x10]` là pointer hợp lệ mới đọc IL:

```text
dq @r8+10 L1
db poi(@r8+10) L20
```

Tiêu chí xác nhận candidate:

```text
[R8+0x10] = 0x24100669774
[R8+0x18] = 0x14
```

và bytes đầu phải là:

```text
00 7F 01 00 80 04 FE 16 02 00 80 01 6F 03 00 80 0A 2D 01 00
```

Nếu khớp, nâng association:

```text
A9BD9..A9BDE emit transaction -> compileMethod call [0xB]
```

lên CONFIRMED.

Sau đó inspect `[0xC]` và `[0xD]` để tìm method khác:

```text
!tt ABBFC:7C4
r r8

!tt ABC54:16BB
r r8
```

Không giả định layout sâu hơn của `CORINFO_METHOD_INFO` ngoài các offset đã kiểm bằng data thực tế.

---

## Ghi chú sửa sau AK.14/peer review

### Query boundary không phải ranh giới tự nhiên

Event cuối của census bắt đầu tại `0x180007FFF`, size `4`, nên access thực tế kéo sang `0x180008003`. Vì query ban đầu là:

```text
[0x180007000,0x180008000)
```

kết quả `0x444` mô tả aggregate của các access **giao với cửa sổ query**, không chứng minh stream tự nhiên dừng tại page boundary.

Hơn nữa, `TTD.Memory*` đã được quan sát trả access chồng lấn range dù start address nằm ngoài lower bound. Do đó event `0x180007FFF/size4` có thể được tính trong cả query `[0x7000,0x8000)` và query kế `[0x8000,0x40000)`.

### RETRACTED

- Không dùng `0x444` như độ dài tự nhiên của toàn static stream.
- Không cộng cơ học `0x153 + 0xE6 = 0x239` làm positive control cho wide query nếu chưa loại event chồng biên.

Nếu census `0xE6` của `[0x8000,0x40000)` chứa lại event `A9BDB:53F`, union count dự kiến sẽ là:

```text
0x153 + 0xE6 - 1 = 0x238
```

nhưng chỉ được dùng sau khi xác nhận identity của event biên.

### Lệnh phân biệt

```text
dx @$upper = @$cursession.TTD.MemoryForPositionRange(0x180008000,0x180040000,"r",@$e1,@$e2)
dx @$upper.Count()
dx -g @$upper.OrderBy(x => x.Position).Take(8)
dx @$wide = @$cursession.TTD.MemoryForPositionRange(0x180007000,0x180040000,"r",@$e1,@$e2)
dx @$wide.Count()
dx @$wide.Select(x => x.Size).Sum()
dx @$wide.Select(x => x.Address).Min()
dx @$wide.Select(x => x.Address).Max()
```

### Giới hạn độ phủ của trace

Metadata có `10.960` record, trong khi trace có `3.235` compile calls. Ngay cả giả định tối đa mỗi call là một record bảo vệ khác nhau:

```text
3235 / 10960 = 29.516%
```

Vì call census còn chứa method thường và có thể chứa compile lặp, unique protected-method coverage của trace này là **nhỏ hơn hoặc bằng 29,516%**, thực tế thấp hơn. Đây là giới hạn của runtime census, không ảnh hưởng mục tiêu cuối host-only/offline nhưng buộc runtime oracle phải lấy mẫu có chiến lược.