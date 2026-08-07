# Workspace layout

Given this target:

```text
D:\LordsBot-Release\LordsMobileBot.exe
```

all generated data is kept beside it:

```text
D:\LordsBot-Release\
├── LordsMobileBot.exe
├── Dumps\
│   └── LordsMobileBot.exe
└── _dnguard\
    └── LordsMobileBot\
        ├── capture\
        │   ├── current.json
        │   └── sessions\
        │       └── 20260730-215500\
        │           ├── methods\...
        │           ├── capture.log
        │           └── session.json
        ├── index\
        │   ├── by-token\...
        │   ├── index.json
        │   ├── stats.json
        │   └── inconsistent.json
        ├── rebuilt\
        │   ├── LordsMobileBot.rebuilt.dll
        │   ├── rebuilt.txt
        │   ├── semantic-validation.json
        │   ├── semantic-validation.txt
        │   ├── semantic-findings.jsonl
        │   ├── semantic-operands.jsonl
        │   └── semantic-object-proven.jsonl
        ├── logs\
        ├── reports\
        ├── dump\
        └── tools\
```

No generated capture/index/rebuild file is written to `C:\Tools` or to the
console tool's installation directory.
