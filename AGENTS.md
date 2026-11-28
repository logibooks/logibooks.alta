# Logibooks Alta Adapter agent rules

## Copyright headers

Every C# source file must begin with this exact header followed by a blank line. Migration files are exempt.

```csharp
// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of Logibooks Alta adapter
```

Use the same three lines with `#` comments in YAML and dotenv configuration files, including `.env.example` and generated `.env` files.

## Diagnostics

- When a warning, error, or deferral reports missing, invalid, or conflicting data, name the specific field, element, or record and include the relevant record identifier when available. Distinguish missing data from invalid data when the validation can tell them apart. Include the expected format or constraint when it helps locate or fix the problem. Keep secrets and raw payloads out of diagnostics.

## Alta polling

- Treat existing records in `ExpressCargoDeclarationList` as immutable. Queue assignments when a linked DEC first appears or when a linked `IndPostList` row is new or changes. Establish the initial snapshot for each monitored CMR and submit its existing linked assignments.
- Scope both tables by `AVIANUM`. Track IndPost rows by `DOCGID` and compare the assignment fields `Num`, `REESTRGID`, `DECISION`, and `ALLWEIGHT`.
- Select assignments using the table snapshots. Read DEC XML on demand for selected assignments and pending retries. XML changes alone do not queue submissions.
- Match a shipment within the linked DEC by XML `IndGid` and `IndPostList.DOCGID` alone. Reject duplicate matches and send `IndPostList.Num` to Core. Log a missing or mismatched XML `HouseWaybillDetails_PrDocumentNumber` at Debug and continue submission if the assignment is otherwise valid.
