# BƯỚC AK.15 — `ICorJitInfo` proxy, phân loại JIT calls và kiểm tra token đầu vào

Mẫu: `LordsMobileBot.exe`  
Ngày: 2026-08-06

## Mục tiêu

1. Đóng identity của object `ICorJitInfo*` được truyền vào `clrjit`.
2. Phân loại 3.235 `compileMethod` calls theo return site và vùng chứa `ILCode`.
3. Kiểm tra trên nhiều HVM-generated MethodBody xem token đã là CLR metadata token thật hay còn virtual token.
4. Nới static census qua page boundary mà không cộng trùng access chồng biên.
5. Không tiếp tục truy vô hạn riêng consumer của stub 20 byte khi chưa chứng minh nó là đường chính.

## Bằng chứng đầu vào

Tại call `[B]` `ABBAE:FDD`:

```text
RDX = 0x24100668928 ; ICorJitInfo*
R8  = 0xFD8257D600  ; CORINFO_METHOD_INFO*
```

Ba mẫu IL:

```text
[B] ILCode=0x24100668754 size=0x2C return=0x180043FFB
[C] ILCode=0x241011A54B9 size=0x12 return=0x18003FB4F
[D] ILCode=0x2410066A410 size=0x31 return=0x180043FFB
```

20 byte đầu `[B]`:

```text
17 0A 21 02 DE 17 6A 41 02 00 00 73 9C 1D 00 0A 80 ED 88 00
```

Decode chắc chắn đến byte đã có:

```text
17                            ldc.i4.1
0A                            stloc.0
21 02 DE 17 6A 41 02 00 00    ldc.i8 0x000002416A17DE02
73 9C 1D 00 0A                newobj 0x0A001D9C
80 ED 88 00 ??                stsfld, thiếu byte operand cuối
```

`0x0A001D9C` là metadata token thật và khớp token pool `rid=0x1D9C, kind=5`.

## AK.15A — đóng identity của `ICorJitInfo` proxy

### Giả thuyết đặt trước

```text
[@rdx]      = 0x180035100
[vtable+E0] = 0x180007850
```

Trong đó `0x180007850` là resolver slot đã được ghi trong sổ chứng cứ.

### Lệnh

```text
!tt ABBAE:FDD
r rdx,r8
dq @rdx L2
dq poi(@rdx)+0xe0 L1
ln poi(@rdx)
```

### Phán quyết

#### CONFIRMED proxy identity

Chỉ khi cả hai điều kiện khớp:

```text
first qword = 0x180035100
vtable+E0   = 0x180007850
```

Khi đó:

```text
DNGuard/HVM-owned ICorJitInfo proxy -> clrjit!compileMethod
```

được nâng CONFIRMED cho call `[B]`.

#### STRONG nhưng chưa CONFIRMED

Nếu first qword nằm trong HVM image nhưng slot `+E0` chưa khớp hoặc layout khác.

#### RETRACTED

Nếu first qword là CLR/CoreCLR vtable ngoài HVM image hoặc `RDX` không còn là object hợp lệ tại entry.

### Hệ quả kiến trúc

Cần cả hai nguồn:

```text
CORINFO_METHOD_INFO:
  ILCode
  ILCodeSize
  locals
  method/scope

ICorJitInfo proxy:
  resolveToken
  getEHinfo
  các callback JIT khác
```

Một hook `compileMethod` lấy được byte IL đầu vào nhưng chưa tự thay thế việc quan sát token/EH callbacks nếu HVM vẫn dùng virtual token hoặc dynamic EH.

## AK.15B — đóng token thứ hai của call `[B]`

### Lệnh

```text
!tt ABBAE:FDD
db poi(@r8+10) L2C
```

### Tiêu chí đặt trước

Nếu bytes tại offset `0x10..0x14` là:

```text
80 ED 88 00 04
```

thì:

```text
stsfld 0x040088ED
```

được nâng CONFIRMED.

Khi đó call `[B]` chứa hai token thật liên tiếp:

```text
newobj 0x0A001D9C
stsfld 0x040088ED
```

cùng khớp token pool đã giải.

### Cảnh báo

Không quét mọi cửa sổ 4 byte rồi kiểm `0x00800000`. Phải parse IL opcode và chỉ kiểm đúng operand loại metadata token. Immediate `ldc.i4`, `ldc.i8`, branch displacement và switch table không phải token.

## AK.15C — census return sites

Dữ liệu đã nhìn thấy chứng minh `Distinct(ReturnAddress) > 2`, vì ít nhất có:

```text
0x7FFAD484BB06 ; CoreCLR-origin calls đầu trace
0x18003FB4F    ; HVM site A
0x180043FFB    ; HVM site B
```

Do đó tiêu chí “Distinct == 2” không còn hợp lệ cho toàn bộ 3.235 calls.

### Lệnh

```text
dx @$jit = @$cursession.TTD.Calls("clrjit!CILJit::compileMethod")
dx @$jit.Count()
dx @$jit.Select(c => c.ReturnAddress).Distinct().Count()
dx @$site43 = @$jit.Where(c => c.ReturnAddress == 0x180043FFB)
dx @$site3f = @$jit.Where(c => c.ReturnAddress == 0x18003FB4F)
dx @$site43.Count()
dx @$site3f.Count()
dx @$hvmRet = @$jit.Where(c => c.ReturnAddress >= 0x180000000 && c.ReturnAddress < 0x18039BC00)
dx @$hvmRet.Count()
```

Liệt kê return sites nếu count nhỏ:

```text
dx -g @$jit.Select(c => c.ReturnAddress).Distinct()
```

Nếu quá lớn:

```text
dx -g @$jit.Select(c => c.ReturnAddress).Distinct().Take(40)
```

### Phán quyết

#### CONFIRMED

- Count từng return site.
- Tổng số calls có return address nằm trong HVM image.

#### STRONG

Nếu `0x180043FFB` tiếp tục tương quan với ILCode trong HVM heap/arena và `0x18003FB4F` với ILCode trong PE image trên nhiều mẫu.

#### Không được gọi ngay

```text
Count(site43) = số method bảo vệ duy nhất
```

vì:

- cùng method có thể compile nhiều lần/tier;
- site có thể xử lý nhiều subtype;
- cần kiểm `ILCode` region và method handle.

## AK.15D — lấy mẫu năm call tại site `0x180043FFB`

### Chọn mẫu

```text
dx -g @$site43.Take(8)
```

Chọn ít nhất năm `TimeStart` phân bố ở các đoạn khác nhau của trace, không chỉ năm call kề nhau.

### Với mỗi entry

```text
!tt <TIMESTART>
r rdx,r8,r9
dq @r8 L4
dq @r8+10 L1
dd @r8+18 L1
```

Đọc đúng `ILCodeSize` đã thấy. Nếu size nhỏ hơn hoặc bằng `0x80`:

```text
db poi(@r8+10) L<SIZE_HEX>
```

Nếu lớn hơn, dump theo các block nhỏ hoặc dùng `.writemem` với end address đã tính rõ; không đoán length.

Ghi cho mỗi mẫu:

```text
TimeStart
ReturnAddress
RDX proxy pointer
method handle
scope
ILCode pointer
ILCodeSize
ILCode region: HVM heap / PE image / other
parsed token operands
ldc.i8 constants có dạng pointer candidate
```

### Phán quyết token

#### STRONG hướng “token đã resolve trước compileMethod”

Nếu toàn bộ metadata-token operands đã parse trong năm HVM-arena samples là token CLR hợp lệ và khớp metadata/pool, đồng thời không thấy virtual format `table | 0x00800000 | key` tại operand boundary.

#### CONFIRMED cho sampled calls

Chỉ phát biểu ở phạm vi mẫu:

```text
Các sampled HVM-generated MethodBody đi vào clrjit với token thật.
```

#### Không được nâng toàn corpus

Năm mẫu không chứng minh tất cả 10.960 record đều như vậy. Muốn tool runtime tổng quát vẫn phải:

- capture `CORINFO_METHOD_INFO`;
- log proxy `resolveToken` calls;
- đối chiếu method handle/scope;
- lấy locals và EH.

#### Virtual token còn tồn tại

Nếu một operand metadata-token đã parse có virtual bit/schema HVM, chuyển sang hook/log proxy `resolveToken` và ghi input/output cho chính call đó.

## AK.15E — kiểm tra `ldc.i8` pointer candidate

Giá trị ở `[B]`:

```text
0x000002416A17DE02
```

không nằm trong HVM arena/PE interval đã biết chỉ vì cùng prefix `0x241`.

### Lệnh tại thời điểm `[B]`

```text
!tt ABBAE:FDD
!address 0x000002416A17DE02
dq 0x000002416A17DE02 L2
```

Nếu `!address` không map hoặc `dq` lỗi, không gọi nó là pointer sống.

### Phán quyết

- Mapped readable address: pointer/cookie candidate STRONG; còn phải xem consumer.
- Unmapped: không phải pointer dereferenceable tại thời điểm đó; có thể là encoded immediate.
- Chỉ khi nhiều methods có mapped session-specific constants mới kết luận verbatim lifting cần relocation/patch.

## AK.15F — wide static census qua page boundary

### Vấn đề overlap

Event:

```text
Address=0x180007FFF Size=4
```

giao với cả hai range kề:

```text
[0x7000,0x8000)
[0x8000,0x40000)
```

Do `TTD.Memory*` trả overlapping accesses, hai census riêng có thể cùng tính event này. Vì vậy không dùng `0x153 + 0xE6 = 0x239` làm positive control chưa kiểm chứng.

### Lệnh

```text
dx @$upper = @$cursession.TTD.MemoryForPositionRange(0x180008000,0x180040000,"r",@$e1,@$e2)
dx @$upper.Count()
dx -g @$upper.OrderBy(x => x.Address).Take(8)
dx @$wide = @$cursession.TTD.MemoryForPositionRange(0x180007000,0x180040000,"r",@$e1,@$e2)
dx @$wide.Count()
dx @$wide.Select(x => x.Size).Sum()
dx @$wide.Select(x => x.Address).Min()
dx @$wide.Select(x => x.Address).Max()
```

### Tiêu chí

- Nếu `$upper` chứa lại event `A9BDB:53F / 0x180007FFF / size4`, hai count cũ double-count một access; với đúng một duplicate biên, wide count dự kiến là `0x238`, không phải `0x239`.
- Nếu không chứa event đó, giải thích semantics trước khi dùng bất kỳ expected count nào.
- `Sum(Size) == end-min` chỉ là aggregate invariant; pairwise adjacency vẫn cần validator.

### Section identity

```text
!dh 0x180000000 -s
```

Tra section chứa RVA `0x7000`. Nếu là executable `.text`, kết luận được nâng:

```text
HVM đọc bytes từ chính native code image như control data/microprogram source.
```

Không gọi đó là per-method bytecode nếu chưa có cross-method evidence.

## Giới hạn độ phủ

```text
compile calls = 3235
metadata records = 10960
upper bound = 29.516%
```

Vì census gồm method thường và có thể compile lặp, unique protected-method coverage thực tế thấp hơn. Runtime hook là oracle/capture path; mục tiêu cuối vẫn là tái tạo host-only/offline, không phụ thuộc việc phủ hết bằng chạy chương trình.

## Ưu tiên sau AK.15

1. Đóng proxy vtable và resolver slot.
2. Dump đủ body `[B]`, xác nhận `0x040088ED`.
3. Count hai HVM return sites.
4. Lấy năm mẫu `site43`, parse token operands đúng opcode.
5. Chỉ sau đó quyết định:
   - tập trung hook `compileMethod` + proxy callbacks; hoặc
   - tiếp tục reverse token/ISA path.
6. Consumer của stub `0x24100669774` giữ trong backlog, không còn là ưu tiên chính.