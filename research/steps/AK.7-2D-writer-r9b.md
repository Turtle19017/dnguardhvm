# BƯỚC AK.7 — Writer `2D` dùng `R9B`

Mẫu: `LordsMobileBot.exe`  
Ngày: 2026-08-06

## Dữ liệu thô

Tại `A9BDD:15CF`:

```asm
00000001`8020333b 44888c505410ffff
mov byte ptr [rax+rdx*2-0EFACh],r9b
```

Registers:

```text
RAX = 0x24100669785
RDX = 0x77D6
R9  = 0xFFFFFFFF0000772D
```

Effective destination:

```text
RAX + RDX*2 - 0xEFAC = 0x24100669785
R9B = 0x2D
```

Trong transition `A9BDD:659 -> A9BDD:15D0`:

```text
IL-buffer reads   = 0
stack/state reads = 0x256 = 598
static-page reads = 0
```

## Cảnh báo về `ub`

Disassembly tuyến tính phía trước writer có candidate:

```asm
mov r9b,byte ptr [rdi+rbp*4-0C2A956h]
```

nhưng cũng có:

```asm
jne 0x18028BEBB
mov byte ptr [...],r9b
```

Tại writer, flags hiển thị `nz`. Vì vậy không được coi listing tuyến tính này là execution provenance. Writer có thể được đi vào từ một edge khác của threaded CFG.

## Kết luận

### CONFIRMED

- `IL[17] = 0x2D` được commit trực tiếp từ `R9B`.
- Effective destination là `0x24100669785`.
- Không có read IL buffer trong transition.
- Có 598 read từ stack/state page trong cùng transition.

### STRONG

- Transition `6F -> 2D` là stack/state-driven phase.
- Byte `2D` đã được materialize trong register state trước khi commit.

### UNPROVEN

- Exact writer gần nhất của `R9B`.
- Exact seed hoặc memory source tạo `R9B = 0x2D`.
- Candidate load tại `0x1802032F8` có thực sự chạy trên current path không.

## Bước tiếp theo — AK.8

Kiểm tra candidate load trong stack-read trace:

```text
dx @$r9cand = @$rst2d.Where(x => x.IP == 0x1802032F8)
dx @$r9cand.Count()
dx -g @$r9cand
```

Sau đó reverse-step theo execution path thật:

```text
!tt A9BDD:15CF
r rip,r9,efl
t-
r rip,r9,efl
u @rip L1
```

Lặp `t-` thủ công và dừng tại instruction đầu tiên ghi `R9/R9D/R9W/R9B` hoặc khi low byte của `R9` đổi khỏi `0x2D`.

## Artifact

```text
Pasted text(20260806-065117).txt
246 dòng raw WinDbg TTD
```
