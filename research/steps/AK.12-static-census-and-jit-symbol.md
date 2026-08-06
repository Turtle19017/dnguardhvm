# BƯỚC AK.12 — Static-page census, IL-buffer history và compileMethod symbol

Mẫu: `LordsMobileBot.exe`  
Ngày: 2026-08-06

## Câu hỏi

1. Census AG cũ `0x153` static-page read events có tái lập không?
2. Trong transaction hiện tại có duplicate start address không?
3. Buffer IL đã biết có bao nhiêu write trên toàn trace, và bao nhiêu write thực sự thuộc HVM emit?
4. Symbol chính xác của JIT entry là gì?

## Dữ liệu thô

Cửa sổ:

```text
A9BD9:0 -> A9BDE:0
address range [0x180007000,0x180008000)
```

Kết quả:

```text
Count                 = 0x153 = 339
Sum(Size)             = 0x444 = 1092
Distinct start address= 0x153 = 339
Min start             = 0x180007BBF
Max start             = 0x180007FFF
Duplicate starts      = 0
```

Đối chứng `Count == 0x153` khớp census AG cũ.

Bảng sort theo address nhìn thấy trong transcript bắt đầu tại `0x180007BBF` và các event hiển thị nối tiếp theo size ở đoạn đã in. Tuy nhiên toàn bảng bị debugger rút gọn bằng `[...]`, nên chưa nâng toàn-range exact adjacency thành CONFIRMED chỉ từ transcript này.

## Phán quyết static reads

### CONFIRMED

- Transaction hiện tại có đúng 339 static-page read events.
- Mỗi event có start address riêng; không có duplicate start address.
- Vì vậy không có reread theo tiêu chí hẹp “cùng start address” trong transaction này.

### Không được suy

- `Distinct start == Count` không chứng minh mỗi byte chỉ được đọc một lần; multi-byte reads vẫn có thể overlap.
- Không có duplicate start trong một transaction không bác hoặc xác nhận shared control data giữa nhiều methods.
- Cross-method reuse chỉ có thể kiểm tra sau khi enumerate ít nhất hai compileMethod invocations.

## IL-buffer history

Query toàn trace vào range:

```text
[0x24100669774,0x24100669788)
```

trả:

```text
Count = 0xD = 13 write events
```

Hai event đầu xảy ra tại `A3A0:5FE` và `A3A0:5FF`, do IP ngoài HVM image (`0x7FFB...`), size 16, ghi dữ liệu UTF-16 vào vùng memory chồng lấn buffer. Chúng là lịch sử sử dụng heap address trước HVM emit, không phải MethodBody writes.

Mười một event còn lại thuộc HVM transaction:

```text
3 operand/token writes:
  IL+2  = 0x04800001
  IL+8  = 0x01800002
  IL+13 = 0x0A800003

8 skeleton/control writes:
  IL+0  = 00
  IL+1  = 7F
  IL+6  = FE
  IL+7  = 16
  IL+12 = 6F
  IL+17 = 2D
  IL+18 = 01
  IL+19 = 00
```

### CONFIRMED

- Toàn memory history có 13 overlapping writes.
- HVM emit phase có đúng 11 writes, khớp census cũ.
- Hai write thừa là pre-HVM reuse của cùng heap address.

### Hệ quả

- Không được dùng raw `TTD.Memory(...).Count()` mà không lọc position/IP để đếm emitter writes.
- Heap address của generated IL buffer đã được dùng cho dữ liệu khác trước transaction; địa chỉ buffer không có identity ổn định toàn trace.

## JIT symbol

Symbol đã resolve:

```text
0x00007FFA_D4EFD490
clrjit!CILJit::compileMethod
```

Signature:

```text
CILJit::compileMethod(
    ICorJitInfo *,
    CORINFO_METHOD_INFO *,
    unsigned int,
    unsigned char **,
    unsigned int *)
```

## Kết luận AK.12

### CONFIRMED

- Census static read `0x153` được tái lập.
- Không có duplicate read start address trong current transaction.
- Current HVM emit vẫn có đúng 11 writes; count 13 toàn trace gồm hai pre-HVM overlapping writes.
- Exact JIT entry symbol/address đã resolve.

### STRONG

- Current transaction đọc một long forward-address span without duplicate starts, phù hợp với một single-pass traversal hơn loop/reread theo cùng start address.

### UNPROVEN

- Toàn 339 reads tạo một exact gap-free, overlap-free interval; cần full adjacency validator hoặc ít nhất last-event size plus pairwise check.
- Trace có bao nhiêu compileMethod invocations.
- Có user virtualized method nào ngoài injected cctor stub hay không.
- Cross-method reuse của static image data.

## Bước tiếp theo — AK.13

Enumerate JIT transactions bằng symbol chính xác:

```text
dx @$jitcalls = @$cursession.TTD.Calls("clrjit!CILJit::compileMethod")
dx @$jitcalls.Count()
dx -g @$jitcalls
```

Nếu string symbol không được chấp nhận:

```text
dx @$jitcalls = @$cursession.TTD.Calls(0x7ffad4efd490)
dx @$jitcalls.Count()
dx -g @$jitcalls
```

Không seek khi query rỗng hoặc lỗi.

Đồng thời đóng aggregate endpoint của static census:

```text
dx @$lastStatic = @$all.OrderBy(x => x.Address).Last()
dx @$lastStatic
dx @$staticEnd = @$lastStatic.Address + @$lastStatic.Size
dx @$staticEnd
dx @$staticEnd - @$all.Select(x => x.Address).Min()
```

`staticEnd - Min == Sum(Size)` chỉ cho thấy aggregate tương thích với exact tiling; không thay pairwise gap/overlap validation.
