# <img src="Logibooks.Alta.Service/Assets/Logibooks.Alta.Adapter.png" alt="Logibooks Alta Adapter" width="15"> Logibooks Alta Adapter

[![CI](https://github.com/logibooks/logibooks.alta/actions/workflows/ci.yml/badge.svg)](https://github.com/logibooks/logibooks.alta/actions/workflows/ci.yml)
[![Release](https://github.com/logibooks/logibooks.alta/actions/workflows/release.yml/badge.svg)](https://github.com/logibooks/logibooks.alta/actions/workflows/release.yml)

Logibooks Alta Adapter is a Windows application that monitors one or more Microsoft SQL Server databases and communicates with Logibooks Core over authenticated HTTPS.

## Runtime behavior

- The self-contained x64 adapter runs as the `LogibooksAltaAdapter` Windows service under `LocalService` or in a console under the current Windows account.
- Production SQL Server instances and databases remain external and are not installed or managed by the adapter.
- In development, run the adapter on Windows with SQL Server 2025 and Adminer in Docker Desktop's Linux containers.
- Quartz runs an initial cycle on enabled startup, then follows the configured cron and time zone. A running cycle causes overlapping firings to be skipped.
- With Core enabled, each cycle refreshes the CMR worklist and reads the matching Alta declarations and IndPost parcels. For queued assignments and retries, the adapter reads the declaration XML in `Docs.BLOB` and matches the shipment by its `IndGid` and the IndPost `DOCGID`. It sends `IndPostList.Num` and all goods lines in XML order, using each line's total in the invoice currency. Missing or mismatched XML parcel numbers are logged at Debug. Ambiguous or incomplete assignments stay pending. The adapter keeps the monitoring baseline in memory.
- With Core disabled, enabled SQL targets receive a connectivity probe. Target concurrency is bounded in either mode.
- Transient GET failures receive up to three attempts with 1- and 2-second delays. An explicit HTTP `401` permits one fresh-login retry. A submission with an unknown outcome can be retried during a later eligible cycle.
- Configuration reload applies schedule changes without rebuilding the monitoring baseline. Changing the SQL source, Core URL, or Core account starts a fresh baseline for that scope.
- Each source declaration (`DOCGID`) is parsed once per successful SQL read when it has due work. Alta sends one Core request with that declaration's ready assignments. Source declarations that share a registration number each use their own request.
- Enable submissions after Core and the adapter support the array-based Alta batch contract.
- Logs identify database targets by configured name but sanitize connection strings, SQL passwords, Core passwords, and JWTs.

