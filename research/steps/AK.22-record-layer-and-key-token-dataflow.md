# BÆ¯á»šC AK.22 â€” Record layer toÃ n corpus vÃ  exact KEYâ†’token data-flow

Máº«u: `LordsMobileBot.exe`  
NgÃ y: 2026-08-06  
Tiáº¿p ná»‘i: AK.21, commit `4b24cbabe20ad154d2ad928817f31c7463817a4c`

> [!IMPORTANT]
> ## CURRENT CANONICAL STATUS
>
> File nÃ y lÃ  tráº¡ng thÃ¡i canonical má»›i nháº¥t.
>
> AK.21 giá»¯ vai trÃ² lá»‹ch sá»­: giáº£ thuyáº¿t payload, raw-grep Ã¢m, cache lineage vÃ  cÃ¡c phÃ©p Ä‘o dáº«n tá»›i káº¿t quáº£ hiá»‡n táº¡i. CÃ¡c overclaim cÅ© á»Ÿ AK.21 Â§1â€“Â§13 Ä‘Æ°á»£c thay tháº¿ bá»Ÿi phÃ¢n loáº¡i trong tÃ i liá»‡u nÃ y.

---

## 1. Pháº¡m vi Ä‘Ãºng cá»§a káº¿t luáº­n `items[]` vÃ  S1

BiÃªn quan sÃ¡t:

```text
items[] = (tag << 24) | low24
low24 max = 0x2CD1
S1 size   = 0x2CD8
```

KEY 1 cá»§a method máº«u Ã¡nh xáº¡ tá»›i:

```text
KEY 1 â†’ CLR token 0x040088ED
RID   = 0x88ED > 0x2CD1
```

Äiá»u nÃ y bÃ¡c mÃ´ hÃ¬nh trá»±c tiáº¿p:

```text
KEY k â†’ record.items[k-1] â†’ real CLR operand token
```

S1 Ä‘Ã£ cÃ³ cÃ¡c signature chá»©a compressed `TypeDefOrRef` coded indices, vÃ­ dá»¥:

```text
12 82 31 â†’ TypeRef 0x0100008C
12 BB CC â†’ TypeDef 0x02000EF3
```

PhÃ¡n quyáº¿t:

```text
CONFIRMED
  S1 signature blobs cÃ³ thá»ƒ chá»©a metadata type references
  dÆ°á»›i dáº¡ng compressed TypeDefOrRef coded indices.

REFUTED
  items[] lÃ  báº£ng KEYâ†’real operand token trá»±c tiáº¿p.

STRONG
  items[] trá» vÃ o signature/type arena S1.

UNPROVEN
  Type references trong S1 liÃªn há»‡ tháº¿ nÃ o vá»›i virtual-token operands
  vÃ  ordered KEYâ†’real-token map cá»§a cÃ¹ng method.
```

Láº­p luáº­n phÃ¢n bá»‘ `tag5` khÃ´ng Ä‘Æ°á»£c dÃ¹ng lÃ m chá»©ng minh báº¥t kháº£ thi; nÃ³ chá»‰ lÃ  thá»‘ng kÃª corpus.

---

## 2. CONFIRMED â€” record layer khá»›p toÃ n bá»™ 10.960 row

Validator tÃ¡i láº­p:

```text
research/tools/ak21_validate_record_layer.py
```

Raw header Ä‘Æ°á»£c Ä‘á»c trá»±c tiáº¿p tá»« `md_full.bin`:

```text
recOff+0x00  u8  maxStack
recOff+0x01  u24 codeSize
recOff+0x04  u16 itemBytes
recOff+0x06  u16 ehCount
recOff+0x08  u16 itemCount
recOff+0x0A  u16 ehDataSize
```

Káº¿t quáº£:

```text
CSV.maxStack == raw.maxStack                    mismatch 0
CSV.codeSize == raw.codeSize                    mismatch 0
CSV.nLocals  == raw.itemCount                   mismatch 0
CSV.ehCount  == raw.ehCount                     mismatch 0
raw.itemBytes == 4 * raw.itemCount              mismatch 0
nextRecOff-recOff == 12+itemBytes+ehDataSize    mismatch 0
ilOffset[i+1] == ilOffset[i] + codeSize[i]       mismatch 0
record bounds                                  mismatch 0
```

Record cuá»‘i:

```text
lastRecordEnd = 0x57C08 = S0 size
```

PhÃ¡n quyáº¿t:

```text
CONFIRMED
  CSV.nLocals == raw.itemCount trÃªn toÃ n corpus.
  raw.itemBytes == 4 * raw.itemCount.
  recordSize == 12 + itemBytes + ehDataSize.
  10.960 record phÃ¢n hoáº¡ch kÃ­n S0 [0,0x57C08).
  ilOffset lÃ  cumulative codeSize trÃªn toÃ n bá»™ 10.959 cáº¡nh.

UNPROVEN
  ilOffset lÃ  raw offset tháº­t trong pl_full.bin.
  Record cá»§a 0x060008E1 cÃ³ itemCount=0.
```

KhÃ´ng cÃ³ opcode local khÃ´ng chá»©ng minh signature-item list rá»—ng; method máº«u váº«n chÆ°a join vá»›i record cá»¥ thá»ƒ.

### Lá»‡nh tÃ¡i láº­p

```powershell
python .\research\tools\ak21_validate_record_layer.py `
  --md "C:\hvm\md_full.bin" `
  --csv "C:\hvm\methods.csv" `
  --payload "C:\hvm\pl_full.bin" `
  --s2 "C:\hvm\s2.bin" `
  --host "D:\LordsBot-Release\LordsMobileBot.exe" `
  --s0-size 0x57C08 `
  --out "C:\hvm\ak21_offline_validation.json"
```

---

## 3. Artifact provenance vÃ  positive controls

```text
md_full.bin
  size   0x1E06E8
  sha256 809333cb66fb64622e7af9f5f1d32836cbca3b19ba66f7c0c41d34caa0a62284

methods.csv
  size   0x4D76F
  sha256 49aa99ed2e1223577905e46b86902b0e194cf7fdb74dc93204166bbe14a3743a

pl_full.bin
  size   0x346C10
  sha256 1e007a5b90f5bf8afa2ea86c248a877cda6156233859fe35e5d9d4646e0ed3e7

s2.bin
  size   0x185E08
  sha256 190c4e8200e2cff577a3085a885f3fa7c99498e6559273fb9dcc153a0ea5ac25

LordsMobileBot.exe
  size   0x0D390628
  sha256 2178bc077a362a18dbdc5b141478740f0870fedc2774e2ed0d741c606f318a0e
```

Positive controls:

```text
md_full.bin first 16-byte anchor: offset 0, count 1
pl_full.bin first 16-byte anchor: offset 0, count 1
s2.bin first 16-byte anchor: offset 0, count 1
LordsMobileBot.exe: MZ at 0, PE signature at file offset 0xF0
S2: 399234 / 399234 DWORD cÃ³ observed tag nibble 0xA
```

Raw grep Ã¢m tÃ­nh chá»‰ Ã¡p dá»¥ng cho cÃ¡c anchor cá»§a method `0x060008E1`; khÃ´ng tá»•ng quÃ¡t hÃ³a sang toÃ n corpus.

---

## 4. Census `codeSize/maxStack` â€” káº¿t luáº­n Ä‘Ãºng má»©c

Quan sÃ¡t:

```text
codeSize 0x2D: 71 record, 69 record EH=0
codeSize 0x19: 31 record, táº¥t cáº£ EH=0
khÃ´ng record nÃ o trong hai táº­p Ä‘á»“ng thá»i cÃ³ maxStack=8
```

```text
CONFIRMED
  Bá»™ lá»c káº¿t há»£p codeSizeâˆˆ{0x2D,0x19} + maxStack=8 + EH=0
  tráº£ 0 candidate.

UNPROVEN
  codeSize giáº£ Ä‘á»‹nh, runtimeâ†”record maxStack equality,
  hay phÃ©p join lÃ  thÃ nh pháº§n tháº¥t báº¡i.
```

`methods.csv.maxStack == raw.maxStack` Ä‘Ã£ Ä‘Æ°á»£c xÃ¡c nháº­n toÃ n corpus. ChÆ°a biáº¿t raw-record `maxStack` cÃ³ khá»›p runtime sample vÃ¬ chÆ°a join record.

---

## 5. Tiny format vÃ  UserString

Tuple runtime:

```text
codeSize=0x2D, maxStack=8, EH=0, khÃ´ng tháº¥y local opcode
```

chá»‰ tÆ°Æ¡ng thÃ­ch tiny format; fat header váº«n cÃ³ thá»ƒ biá»ƒu diá»…n cÃ¹ng giÃ¡ trá»‹.

```text
STRONG
  Sample tÆ°Æ¡ng thÃ­ch CorILMethod_TinyFormat.

UNPROVEN
  Original protected MethodBody dÃ¹ng tiny header.
```

Wrapper cÃ³ conditional custom path cho UserString khi `[proxy+0x78] != 0`:

```text
CONFIRMED
  Conditional custom UserString path tá»“n táº¡i.

STRONG
  DNGuard cÃ³ thá»ƒ báº£o vá»‡/biáº¿n Ä‘á»•i #US qua path nÃ y.

UNPROVEN
  Sample UserString cá»¥ thá»ƒ vÃ  representation offline cá»§a #US.
```

---

## 6. CONFIRMED â€” ordered KEYâ†’real-token map

Táº¡i `AD7BF:32`:

```text
head = 0x24100668DA0
head+0x00 = 0x24100668CE0   leftmost
head+0x08 = 0x24100668D10   root
head+0x10 = 0x24100668D40   rightmost
```

Node layout:

```text
+0x00 left
+0x08 parent
+0x10 right
+0x18 u32 KEY
+0x1C u32 real CLR metadata token
```

Mappings quan sÃ¡t:

```text
node 0x24100668CE0: KEY 1 â†’ 0x040088ED
node 0x24100668D10: KEY 2 â†’ 0x010004C9
node 0x24100668D40: KEY 3 â†’ 0x0A001D99
```

CÃ¢y cÃ³ root KEY 2, left KEY 1, right KEY 3. Táº¡i position nÃ y khÃ´ng cÃ³ non-sentinel node KEY 4â€“6.

```text
CONFIRMED
  ÄÃ¢y lÃ  ordered KEYâ†’real-token map cho transaction/lifetime quan sÃ¡t.

STRONG
  Implementation tÆ°Æ¡ng thÃ­ch MSVC std::_Tree/std::map<u32,u32>.

UNPROVEN
  Lifetime/scope toÃ n method vÃ  vá»‹ trÃ­ KEY 4â€“6.
```

---

## 7. CONFIRMED â€” exact map value â†’ EAX â†’ R14D

Read watchpoint trÃªn `0x24100668CFC` báº¯t exact load:

```asm
0x18000587F  mov eax,dword ptr [rbx+1Ch]
```

Vá»›i:

```text
RBX        = 0x24100668CE0
[RBX+18h]  = 1
[RBX+1Ch]  = 0x040088ED
```

HÃ m epilogue rá»“i `ret` vá»:

```asm
0x180378851  mov r14d,eax
```

Data dependency:

```text
map[KEY 1].value = 0x040088ED
  â†’ EAX = 0x040088ED
  â†’ R14D = 0x040088ED
```

Metadata token khÃ´ng thuá»™c output ABI cá»§a `CORINFO_RESOLVED_TOKEN`; output ABI lÃ  handles/spec blobs. Tuy nhiÃªn helper ná»™i bá»™ giá»¯ real token trong EAX/R14D trÆ°á»›c cache layer.

---

## 8. CONFIRMED â€” R14D + resolver mask â†’ masked-real R8D

Táº¡i `AD7BF:30..32`:

```asm
0x180379287  mov r8d,dword ptr [rax+30h]
0x18037928B  xor r8d,r14d
0x18037928E  mov dword ptr [rsp+150h],r8d
```

Vá»›i:

```text
RAX          = resolver state 0x24100666840
[RAX+30h]    = 0x6A714B62
R14D         = 0x040088ED
R8D sau XOR  = 0x6E71C38F
```

```text
maskedReal = resolverMask XOR realToken
0x6E71C38F = 0x6A714B62 XOR 0x040088ED
```

Viá»‡c stack slot trÆ°á»›c Ä‘Ã³ tá»«ng chá»©a raw virtual token chá»‰ lÃ  storage lifecycle. Báº±ng chá»©ng semantic náº±m á»Ÿ chain register/instruction trÃªn; khÃ´ng dÃ¹ng tá»« `scrub`.

KEY 2 xÃ¡c nháº­n cÃ¹ng mask:

```text
0x6B714FAB XOR 0x6A714B62 = 0x010004C9
```

---

## 9. Pipeline runtime canonical cho KEY 1

```text
virtual token 0x04800001
    â†“ KEY extraction / lookup call context
ordered KEYâ†’real-token map
    â†“ node KEY 1, value 0x040088ED
mov eax,[node+1Ch]
    â†“
EAX = 0x040088ED
    â†“ mov r14d,eax
R14D = real CLR token
    â†“ mov r8d,[resolverState+30h]
    â†“ xor r8d,r14d
R8D = masked-real 0x6E71C38F
    â†“ cache lookup 0x1800021B0, key (module, masked-real)

cache hit:
    node handles â†’ output

cache miss:
    request.token ^= resolverMask
    â†“ real CLR token 0x040088ED
    â†“ underlying CoreCLR CEEInfo::resolveToken
    â†“ cache insert 0x1800058A0

runtime handles
    â†“ 0x38-byte copy into CORINFO_RESOLVED_TOKEN+0x18
```

Offline rebuilder khÃ´ng cáº§n mÃ´ phá»ng runtime handle cache. Cáº¡nh báº¯t buá»™c lÃ  nguá»“n host/offline dá»±ng:

```text
method/KEY ™X[ÓˆY]Y]HÚÙ[‚˜‚‹KKB‚ˆÈÈLˆÕT”‘S•ÕUTÂ‚˜^ÓÓ‘’T“QQˆÌHÚYÛ˜]\™H›ØœÈğìÈÛÛ\™\ÜÙY\QY“Ü”™Yˆ™Y™\™[˜Ù\Ë‚ˆ][\Ö×HÚ0í™È8n¨ÚH°è›™ÈÑVx¡¤œ™X[]ÚÙ[ˆ¸nìXÈxn¯Ü‚ˆÌ™XÛÜ™^[İ]°èÔÕˆ›Ú™Xİ[Ûˆ°ê›ˆğèˆ¸næHLMŒ™XÛÜ™‚ˆ“ØØ[ÈOH˜]Ëš][PÛİ[È][P]\ÈOH
š][PÛİ[‚ˆ™XÛÜ™Ú^™HHLŠÚ][P]\ÊÙZ]TÚ^™NÈÛİ™\˜YÙH1$pî›™ÈMĞÌ‚ˆ[Ù™œÙ]™Xİ\œ™[˜ÙH1$pî›™Èğèˆ¸næHLMNHøn¨[š‚ˆÜ™\™YÑVx¡¤œ™X[]ÚÙ[ˆX\›ÙH
ÌNÑVHÈ
ÌPÈÚÙ[‹‚ˆÑVLx¡¤ŒQÑVL¸¡¤ŒLÎKÑVLø¡¤ŒLQNK‚ˆX\˜[Yx¡¤‘PV8¡¤”ŒM^Xİ]KY›İË‚ˆVÜ™\ÛÛ™\”İ]JÌÌHÔˆŒM^Xİ]KY›İË‚ˆ[™HØXÚHÙ^H
[Ù[KX\ÚÙY\™X[
KÛÜ™PÓˆ[YØ][Ûˆ°èÎX]Hİ]]ÛÜK‚‚”Õ“Ó‘Âˆ^[ØY0î[™È[˜ÛÙYÜİXİ\™Y™\™\Ù[][Û‹‚ˆÑVHØÛÜHønéXÈ¸næH[ÈY]Ùİ˜[œØXİ[Û‹‚ˆ™\ÛÛ™\ˆX\ÚÈØÛÜH[È™\ÛÛ™\‹Ü›ŞH[œİ[˜ÙK‚‚•S”“Õ‘S‚ˆ^XİÛİ\˜ÙHÜİÛÙ™›[™H8nì[™ÈÑVx¡¤œ™X[]ÚÙ[ˆX\‚ˆÑVH^˜Xİ[Ûˆ8nêÈš\X[ÚÙ[ˆ8nçÈÛÚİ\Ø[\Ú]K‚ˆÑVH8 $Íˆ°èY™][YKÛX\İYÙHønéØHİY™š^‚ˆ™XÛÜ™ønéØHŒLH°è’Q8¡¤œ™XÛÜ™[™^‚ˆ^[ØYÔÌˆ™\™\Ù[][Ûˆ°èX\ÚÈÛİ\˜ÙHÙ™›[™K‚˜‚‹KKB‚ˆÈÈLKˆ8nêH8nìHxn¯Ü[Â‚Ÿ8n¨[™ÈšxnáØÈxnéXÈpêHŸKK_KK_KK_ŸH^HØ[\‹ÜÛİ\˜ÙH8nì[™ÈÜ™\™YÑVx¡¤œ™X[]ÚÙ[ˆX\1$0ìÛ™È™İxnäÛˆÙ™›[™H]X[ˆ¸nã[™Èš8n©]Ÿˆ¸n«İÑVH^˜Xİ[Ûˆ°èÛÛ\\˜]Ü‹ÛÛÚİ\[œ]ğèˆ8n©]š\X[ÚÙ[ˆ8¡¤ˆÑVH8¡¤ˆ›ÙHŸÈÚxnàÛHX\8nçÈğèXÈY™][YKÜÜÚ][Ûˆ]xnæ[ˆ1$xnàÈ0ëHÑVH8 $Íˆğèˆ0èšY]ÙÜ˜XÛHŸ1$8n¯ÛHY]ÙYˆ›İÜÈÜİ0èXÈ1$xnâÛšLMŒ™XÛÜ™ğìÈ8nàÈ8néÈğèˆ¸næHY]ÙYˆ^HÚ8nâHxnæ]8n«\ÛÛˆ1$q¬8nèØÈ¸n¨ÛÈ¸náÈŸH›Ú[ˆY]ÙŒLX¸næÚHÌ™XÛÜ™ønäH1$xnâÛš’Q8¡¤œ™XÛÜ™°èšY[Ù[X[XÜÈŸˆ™]™\œÙH^[ØYÔÌˆ¸n¬[™Èøn­ÜY]ÙÒÑVH1$pèÈšxn¯İxn¯Ûˆ8næÚHÜİ[Û›HXÛÙ\ˆ‚”›İÈÛİ[xnæ]pëšÚ0í™È0èXÈ1$xnâÛš8nêH8nìH™XÛÜ™Ú0í™ÈÚ8nê[™ÈZ[š[œÙH[™^°èñj[™ÈÚ0í™ÈÚ8nê[™ÈZ[š\›]]][Û‹‚