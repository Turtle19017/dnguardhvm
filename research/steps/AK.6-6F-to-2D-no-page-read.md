# BƯỚC AK.6 — Transition `6F -> 2D` không đọc static page `0x180007000..0x180008000`

Mẫu: `LordsMobileBot.exe`  
Repo: `Turtle19017/dnguardhvm`  
Ngày: 2026-08-06

## Câu hỏi

1. Sau write `IL[12] = 0x6F` và trước write `IL[17] = 0x2D`, runtime có tiếp tục tiêu thụ reverse static stream trong page `0x180007000..0x180008000` không?
2. Boundary write `2D` có xuất hiện đúng một lần trong cửa sổ không?
3. Ba sample đại diện của AK.5 có tiếp tục xác nhận effective source bằng RBX không?

## Giả thuyết đặt trước

- **H1:** transition `6F -> 2D` tiếp tục dùng một contiguous static slice ngay dưới `0x180007BBF`.
- **H2:** cửa sổ chứa đúng một write vào `IL[17]`, giá trị `0x2D`.
- **H3:** RBX tiếp tục là source cursor tại các sample đại diện của AK.5.

## Lệnh

```text
dx @$p1 = @$create("Debugger.Models.TTD.Position",0xA9BDD,0x659)
dx @$p2 = @$create("Debugger.Models.TTD.Position",0xA9BDD,0x15D0)
dx @$r2d = @$cursession.TTD.MemoryForPositionRange(0x180007000,0x180008000,"r",@$p1,@$p2)
dx @$r2d.Count()
dx -g @$r2d

dx @$w2d = @$cursession.TTD.MemoryForPositionRange(0x24100669785,0x24100669786,"w",@$p1,@$p2)
dx @$w2d.Count()
dx -g @$w2d
```

## Đối chứng dương

Query write IL trong cùng cửa sổ trả về đúng một event:

```text
A9BDD:15CF
address  = 0x24100669785
size     = 1
IP       = 0x18020333B
IL[17]   = 2D
```

```text
@$w2d.Count() = 1
```

Vì boundary write tồn tại đúng vị trí dự kiến, cửa sổ TTD và position range là hợp lệ. Do đó kết quả read rỗng không được xem là lỗi truy vấn chung.

## Dữ liệu thô — static page reads

```text
@$r2d.Count() = 0
```

```text
Warning: The specified container expression is empty.
```

Không có read nào trong:

```text
address range = [0x180007000,0x180008000)
TTD range     = [A9BDD:659,A9BDD:15D0)
```

## Phân tích

AK.2 đến AK.5 đã cho một reverse stream liên tục:

```text
AK.2  [0x180007CAC,0x180007CCE) -> 7F
AK.3  [0x180007C97,0x180007CAC) -> FE
AK.4  [0x180007C7F,0x180007C97) -> 16
AK.5  [0x180007BBF,0x180007C7F) -> 6F
```

AK.6 không tiếp tục bằng một slice ngay dưới `0x180007BBF` trong cùng static page. Vì vậy mô hình:

```text
mỗi write opcode liên tiếp luôn được preceded bởi một contiguous slice
trong page 0x180007000..0x180008000
```

bị bác bỏ ở transition `6F -> 2D`.

Điều này không bác bỏ reverse stream đã xác nhận ở AK.2-AK.5. Nó chỉ chứng minh emit pipeline có ít nhất một phase/transition không cần đọc lại page đó.

Các mô hình còn phù hợp:

1. `2D` được materialize hoàn toàn từ register/flags/stack state đã dựng trước.
2. Runtime đọc state từ một memory region khác, không nằm trong page `0x180007000..0x180008000`.
3. Opcode/branch state đã được staged trước và write `2D` chỉ là commit cuối.

Chưa có dữ liệu để chọn giữa ba mô hình trên.

## Đóng lineage RBX của AK.5

### `A9BDC:1BB6`

```asm
mov rdi,qword ptr [rbx]
```

```text
RBX = 0x180007C77
EA  = RBX
```

**CONFIRMED**.

### `A9BDC:2058`

```asm
mov edx,dword ptr [rdx+rbx-1396407h]
```

```text
RBX = 0x180007C24
RDX = 0x01396407
EA  = RBX + 0x01396407 - 0x01396407 = RBX
```

**CONFIRMED**.

### `A9BDC:259B`

```asm
mov edi,dword ptr [rbx+rdx]
```

```text
RBX = 0x180007BBF
RDX = 0
EA  = RBX
```

**CONFIRMED**.

Ba sample xác nhận RBX/effective source tại đầu, giữa và cuối slice AK.5.

## Kết luận AK.6

### CONFIRMED

- Không có read nào từ page `0x180007000..0x180008000` giữa write `6F` và write `2D`.
- Cửa sổ có đúng một boundary write: `IL[17] = 0x2D` tại `A9BDD:15CF`.
- Kết quả âm có đối chứng dương trong cùng cửa sổ, nên đây là absence có ý nghĩa chứ không phải query hỏng.
- RBX/effective source được xác nhận trực tiếp tại ba sample AK.5: `1BB6`, `2058`, `259B`.

### RETRACTED

- Mọi write opcode liên tiếp đều phân chia một contiguous slice trong page `0x180007000..0x180008000`.
- Có thể tiếp tục suy ngay slice `2D` bằng cách lấy địa chỉ dưới `0x180007BBF` trong page này.

### STRONG

- Emit pipeline có ít nhất hai loại phase:
  1. phase tiêu thụ reverse static stream;
  2. phase phát IL từ state đã materialize hoặc từ nguồn memory khác.
- Transition `6F -> 2D` nhiều khả năng thuộc phase thứ hai.

### UNPROVEN

- `2D` được tạo hoàn toàn từ register state.
- Runtime có đọc một static stream khác ngoài page `0x180007000..0x180008000`.
- `2D` đã được staged từ trước và event `15CF` chỉ commit byte cuối.
- Cơ chế phase switching này áp dụng nguyên vẹn cho method người dùng virtualized.

## Artifact

```text
Raw WinDbg TTD output do người dùng gửi trong phiên ngày 2026-08-06.
```

## Bước phân biệt tiếp theo — AK.7

Truy exact provenance của byte `2D` thay vì tiếp tục giả định có static slice.

### 1. Xem instruction writer và source register

```text
!tt A9BDD:15CF
r
ub @rip L16
u @rip L6
```

### 2. Kiểm tra IL buffer có bị đọc trong transition không

```text
dx @$ril2d = @$cursession.TTD.MemoryForPositionRange(0x24100669774,0x24100669788,"r",@$p1,@$p2)
dx @$ril2d.Count()
dx -g @$ril2d
```

### 3. Kiểm tra stack/state page

```text
dx @$rst2d = @$cursession.TTD.MemoryForPositionRange(0xFD82557000,0xFD82558000,"r",@$p1,@$p2)
dx @$rst2d.Count()
dx -g @$rst2d.Take(80)
```

Sau khi xác định source register của write `2D`, reverse-step tới writer gần nhất của register đó. Không dùng `!tt br-` nếu query trả rỗng hoặc E_NOTIMPL.

Mục tiêu:

```text
exact source/state -> IL byte 2D
```
