# Logibooks Alta Adapter agent rules

## Copyright headers

Every C# source file must begin with this exact header followed by a blank line. Migration files are exempt.

```csharp
// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter
```

Use the same three lines with `#` comments in YAML and dotenv configuration files, including `.env.example` and generated `.env` files.

## Alta polling

- Treat existing records in `ExpressCargoDeclarationList` as immutable. Queue assignments when a linked DEC first appears or when a linked `IndPostList` row is new or changes. Establish the initial snapshot for each monitored CMR and submit its existing linked assignments.
- Scope both tables by `AVIANUM`. Track IndPost rows by `DOCGID` and compare the assignment fields `Num`, `REESTRGID`, `DECISION`, and `ALLWEIGHT`.
- Select assignments using the table snapshots. Read DEC XML and `E2IList` goods on demand to build those submissions, including pending retries. XML and invoice changes alone do not queue submissions.
