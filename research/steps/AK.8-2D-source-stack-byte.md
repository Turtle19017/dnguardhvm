# BƯỚC AK.8 — Exact source của `R9B = 0x2D`

Mẫu: `LordsMobileBot.exe`  
Ngày: 2026-08-06

## Câu hỏi

1. Candidate load tuyến tính tại `0x1802032F8` có thực sự chạy trên current path không?
2. Instruction gần nhất ghi `R9B` trước writer IL là gì?
3. Byte nguồn tạo `IL[17] = 0x2D` nằm ở đâu?

## Dữ liệu thô

Candidate tuyến tính:

```text
dx @$r9cand = @$rst2d.Where(x => x.IP == 0x1802032F8)
@$r9cand.Count() = 0
```

Vì vậy candidate tại `0x1802032F8` không xuất hiện trong stack-read trace của transition hiện tại.

Reverse execution path từ writer:

```text
A9BDD:15CF
0x18020333B  mov byte ptr [rax+rdx*2-0EFACh],r9b
R9B = 2D
```

```text
A9BDD:15CE
0x1802DBBAD  call 0x18020333B
R9B = 2D
```

```text
A9BDD:15CD
0x1802DBBA5  lea rdi,[rdi+rdx-77CCh]
R9B = 2D
```

```text
A9BDD:15CC
0x1802DBB9D  mov r9b,byte ptr [rdi+rdx*4-1DF50h]
resolved source = 0xFD825575A6
source byte     = 0x2D
R9B before load = 0xFF
```

Sau instruction tại `15CC`, low byte của `R9` trở thành `0x2D`; instruction này là writer gần nhất của `R9B` trên execution path thật.

## Exact provenance đã khép

```text
[0xFD825575A6] = 0x2D
        ↓
0x1802DBB9D: mov r9b,byte ptr [...]
        ↓
R9B = 0x2D
        ↓
0x18020333B: mov byte ptr [IL+17],r9b
        ↓
IL[17] = 0x2D
```

## Kết luận

### CONFIRMED

- Candidate `mov r9b,[...]` tại `0x1802032F8` không chạy trong transition hiện tại.
- Writer gần nhất của `R9B` trên current path là `0x1802DBB9D` tại `A9BDD:15CC`.
- Instruction này đọc byte `0x2D` từ `0xFD825575A6`.
- `R9B` đổi từ `0xFF` sang `0x2D` tại instruction này.
- Provenance `stack/state byte -> R9B -> IL[17]` đã khép hoàn toàn.

### RETRACTED

- Linear `ub` candidate tại `0x1802032F8` là source của `2D`.
- `2D` được materialize chỉ bằng arithmetic trên register mà không cần memory read gần writer.

### STRONG

- Transition `6F -> 2D` dùng stack/state byte đã được chuẩn bị trước, sau đó load trực tiếp vào `R9B` và commit vào IL buffer.

### UNPROVEN

- Writer trước đó đã đặt `0x2D` vào `0xFD825575A6` là instruction nào.
- Byte `0x2D` tại stack/state slot này là raw opcode, decoded branch opcode hay state đã tổng hợp.
- Slot `0xFD825575A6` có ý nghĩa ổn định giữa các method/transaction hay chỉ là stack address tái sử dụng.

## Bước tiếp theo — AK.9

Truy writer gần nhất của stack/state byte `0xFD825575A6` trước `A9BDD:15CC`.

```text
dx @$s1 = @$create("Debugger.Models.TTD.Position",0xA9BDD,0x659)
dx @$s2 = @$create("Debugger.Models.TTD.Position",0xA9BDD,0x15CC)
dx @$wsrc2d = @$cursession.TTD.MemoryForPositionRange(0xFD825575A6,0xFD825575A7,"w",@$s1,@$s2)
dx @$wsrc2d.Count()
dx -g @$wsrc2d
```

Nếu `Count() > 0`, lấy event cuối cùng theo thời gian rồi chụp:

```text
!tt <POSITION_CỦA_EVENT_CUỐI>
r
u @rip L3
```

Không seek khi query rỗng.

Mục tiêu:

```text
exact writer -> [0xFD825575A6] = 0x2D
```
