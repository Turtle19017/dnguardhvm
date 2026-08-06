# BƯỚC AK.11 — Capacity, reuse và kiểm tra phạm vi trace

Mẫu: `LordsMobileBot.exe`  
Ngày: 2026-08-06

## Mục tiêu

1. Ghi nhận các retraction từ AK.1 và AK.2–AK.10.
2. Kiểm tra toàn bộ read trong static image page của emit transaction hiện tại.
3. Không suy sai rằng số write vào một IL buffer cụ thể bằng số method đã JIT trong toàn trace.
4. Xác định trace có bao nhiêu invocation `compileMethod` thực sự.

## Retraction đã chấp nhận

### CONFIRMED

- Toàn bộ 7.092 candidate header kiểm tra được đều có `len < 0x100`; bộ lọc cũ không loại một quần thể `u24 len >= 0x100`.
- Mô hình tổng quát `next = p + 8 + align8(u24_len)` vẫn sai vì không tạo được chain hợp lệ tới EOF.
- Phép `0x6A ^ 0x15 = 0x7F` không phải execution provenance và phải rút lại.
- Provenance thực của `0x7F` là chuỗi transform runtime đã single-step; provenance thực của `0x2D` hiện khép tới `BPL 0x5A -> ror 1 -> 0x2D`.

## Đánh giá lập luận dung lượng

Dữ liệu đã có:

```text
AK.2  34 byte static -> boundary emit 7F
AK.3  21 byte static -> boundary emit FE
AK.4  24 byte static -> boundary emit 16
AK.5 192 byte static -> boundary emit 6F
AK.6   0 byte static -> boundary emit 2D
```

Tổng AK.2–AK.5:

```text
271 static bytes / 4 emitted opcode bytes = 67.75
```

`sum(codeSize)` của 10.960 metadata record:

```text
0x2ED00E = 3,067,918 IL bytes
```

Phép ngoại suy thô:

```text
3,067,918 * 67.75 ~= 207.8 MB
```

lớn hơn nhiều ảnh HVMRun64 `0x39BC00 = 3,783,680` byte.

### STRONG

- Dải static image đang được đọc phù hợp hơn với control data/microprogram/native-emitter artifact dùng chung so với một corpus per-method lưu riêng theo tỉ lệ quan sát được.
- Không nên tiếp tục gọi các byte tại `0x180007xxx` là bytecode riêng của method hiện tại nếu chưa có cross-method reuse evidence.

### Không được nâng thành CONFIRMED chỉ từ phép ngoại suy

- Tỉ lệ 67.75 chỉ lấy từ bốn boundary emit của một stub 20 byte; nó không chứng minh mọi IL byte đều tiêu thụ cùng tỉ lệ.
- Variable-length threaded VM hoặc emitter có thể có phase đọc nhiều, phase không đọc, loop và state staging.
- Các run zero trong slice không phân biệt được bytecode với constant/control data.
- Lập luận dung lượng bác mô hình “unique per-method bytes theo tỉ lệ này”, không tự nó bác mọi dạng VM ISA hoặc shared microprogram.

## Sửa hai tiêu chí phán quyết cũ

### 1. `Count() > Distinct(Address).Count()`

Điều này chỉ chứng minh có event dùng lại cùng **địa chỉ bắt đầu** trong cửa sổ. Nó chưa đủ để kết luận:

```text
shared pool giữa nhiều method
```

và cũng chưa đủ để bác một program có loop hoặc overlapping loads.

Muốn chứng minh reuse giữa method phải có ít nhất hai `compileMethod` transaction riêng rồi so sánh dải read của chúng.

### 2. `IL write count == 0xB`

Query write vào riêng buffer:

```text
0x24100669774..0x24100669788
```

chỉ cho biết buffer đã biết có 11 write event. Nó không chứng minh toàn trace chỉ JIT một method, vì method khác có thể dùng buffer khác.

Để đếm method phải enumerate invocation `ICorJitCompiler::compileMethod` hoặc entry hook tương đương. IL trực tiếp nằm trong `CORINFO_METHOD_INFO` truyền vào `compileMethod`; `ICorJitInfo` không phải điểm trực tiếp chính để lấy IL input.

## AK.11A — census static page trong transaction hiện tại

Mỗi lệnh một dòng:

```text
dx @$e1 = @$create("Debugger.Models.TTD.Position",0xA9BD9,0x0)
dx @$e2 = @$create("Debugger.Models.TTD.Position",0xA9BDE,0x0)
dx @$all = @$cursession.TTD.MemoryForPositionRange(0x180007000,0x180008000,"r",@$e1,@$e2)
dx @$all.Count()
dx @$all.Select(x => x.Size).Sum()
dx @$all.Select(x => x.Address).Distinct().Count()
dx @$all.Select(x => x.Address).Min()
dx @$all.Select(x => x.Address).Max()
```

Đối chứng dương đặt trước:

```text
@$all.Count() phải bằng 0x153
```

Nếu khác, dừng diễn giải và kiểm tra lại position range.

Kiểm tra duplicate start address:

```text
dx @$dupStart = @$all.GroupBy(x => x.Address).Where(g => g.Count() > 1)
dx @$dupStart.Count()
dx -g @$dupStart
```

Nếu `GroupBy` trả E_NOTIMPL, không dùng pseudo-register đó; thay bằng:

```text
dx -g @$all.OrderBy(x => x.Address)
```

và phân tích bảng bên ngoài WinDbg.

### Phán quyết

- Duplicate start address > 0: **CONFIRMED** có reread trong transaction hiện tại.
- Không được suy cross-method reuse cho tới khi có transaction thứ hai.
- `Count == Distinct(start address)` cũng không chứng minh mỗi byte chỉ đọc một lần vì các read nhiều byte có thể overlap.

## AK.11B — write census của buffer đã biết

```text
dx @$ilknown = @$cursession.TTD.Memory(0x24100669774,0x24100669788,"w")
dx @$ilknown.Count()
```

Chỉ chạy bảng nếu `Count() <= 0x40`:

```text
dx -g @$ilknown
```

### Phán quyết

- `Count == 0xB`: buffer của stub có đúng 11 write event trên trace.
- Không được kết luận “chỉ một method đã emit” từ query này.

## AK.11C — đếm JIT transaction thực

Trước hết resolve symbol:

```text
x clrjit!*compileMethod*
```

Dùng đúng symbol trả về, không đoán tên:

```text
dx @$jitcalls = @$cursession.TTD.Calls("<EXACT_COMPILEMETHOD_SYMBOL>")
dx @$jitcalls.Count()
dx -g @$jitcalls
```

Nếu `TTD.Calls` không nhận symbol hoặc trả rỗng, tìm entry hook HVM/JIT shim đã dùng trong capture và query đúng địa chỉ/symbol đó. Không seek kết quả rỗng.

### Phán quyết

- Một compile transaction: trace này chưa đủ để kiểm tra cross-method reuse.
- Nhiều compile transaction: tách position range từng call, xác định IL pointer/size và static-page read range của từng method.
- Chỉ khi hai method riêng đọc lại cùng static image interval mới nâng “shared emitter/control artifact” lên **CONFIRMED theo đường cross-method**.

## Ưu tiên dự án

Current transaction là injected cctor stub, chưa phải bằng chứng cho method người dùng virtualized. Sau AK.11, ưu tiên cao nhất là ép JIT một method người dùng có record/payload đã biết rồi bắt `compileMethod` input:

```text
CORINFO_METHOD_INFO.ILCode
CORINFO_METHOD_INFO.ILCodeSize
CORINFO_METHOD_INFO.locals
EH count / EH queries
```

Mục tiêu nghiên cứu runtime là dùng trace như oracle để suy format offline; không sa đà truy vô hạn source của riêng byte `0x5A` nếu chưa có user-method transaction.
