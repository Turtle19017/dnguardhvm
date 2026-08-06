# BƯỚC AK.17 — Không thấy resolver trong hai vtable và chưa lấy được site43 samples

Mẫu: `LordsMobileBot.exe`  
Ngày: 2026-08-06

## Câu hỏi

1. Giá trị `0x180007850` có xuất hiện trực tiếp trong hai bảng bắt đầu tại `0x1800739A8` và `0x1800739E8` trong phạm vi `0x400` byte không?
2. Các lệnh lấy mẫu `@$site43` có thực sự chạy không?
3. Dump register cuối có phải sample mới không?

## Dữ liệu thô

Hai search:

```text
s -q 0x1800739A8 0x180073DA8 0x180007850
s -q 0x1800739E8 0x180073DE8 0x180007850
```

không in ra match.

Các lệnh:

```text
dx -g @$site43.Take(2)
dx -g @$site43.Skip(0x10).Take(1)
...
```

đều trả:

```text
Error: Use of undefined variable
```

Lệnh:

```text
!tt <TIMESTART>
```

trả:

```text
error: Invalid position
```

Sau đó registers vẫn là:

```text
RDX = 0x24100668928
R8  = 0xFD8257D600
R9  = 0xFFFFFFFF
```

và `CORINFO_METHOD_INFO` vẫn là body `[B]`:

```text
ILCode     = 0x24100668754
ILCodeSize = 0x2C
```

## Kết luận

### CONFIRMED

- Không có direct qword match `0x180007850` trong hai phạm vi:

```text
[0x1800739A8,0x180073DA8)
[0x1800739E8,0x180073DE8)
```

- `@$site43` không tồn tại trong debugger state hiện tại; không có sample site43 mới nào được enumerate.
- `!tt <TIMESTART>` dùng placeholder literal nên không seek được.
- Register/body dump cuối chỉ lặp lại call `[B]` tại `ABBAE:FDD`; nó không phải một sample mới.

### RETRACTED

- Không tiếp tục gọi `0x180007850` là direct slot trong một trong hai bảng trên, ít nhất trong phạm vi `+0x400` đã search.

### UNPROVEN

- `0x180007850` có thể vẫn được gọi gián tiếp qua thunk, bảng khác, field khác hoặc code dispatch không nằm trong hai vùng đã search.
- Classifier site43 chưa được kiểm thêm ngoài ba mẫu cũ.

## Bước tiếp theo — khởi tạo lại biến và lấy TimeStart thật

Không tái sử dụng biến undefined. Tạo tên mới:

```text
dx @$jit17 = @$cursession.TTD.Calls("clrjit!CILJit::compileMethod")
dx @$jit17.Count()
dx @$site43_17 = @$jit17.Where(c => c.ReturnAddress == 0x180043FFB)
dx @$site43_17.Count()
```

Đối chứng:

```text
@$jit17.Count()    = 0xCA3
@$site43_17.Count() = 0x52
```

In các row rải đều để lấy `TimeStart` thật:

```text
dx -g @$site43_17.Take(2)
dx -g @$site43_17.Skip(0x10).Take(1)
dx -g @$site43_17.Skip(0x20).Take(1)
dx -g @$site43_17.Skip(0x30).Take(1)
dx -g @$site43_17.Skip(0x40).Take(1)
dx -g @$site43_17.Skip(0x50).Take(2)
```

Sau khi bảng in ra một vị trí thật như `ABC54:16BB`, dùng đúng giá trị đó:

```text
!tt ABC54:16BB
```

Không nhập dấu `< >` và không nhập chữ `TIMESTART`.

Tại mỗi sample:

```text
r rdx,r8,r9
dq @r8 L4
dq @r8+10 L1
dd @r8+18 L1
```

Chỉ đọc IL sau khi đã có pointer và size hợp lệ.
