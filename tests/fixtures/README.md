# Synthetic test fixtures

Every value in these files is invented for testing. They contain **no real patient data**.

* Patient names use the `SYNTHETIC^...` pattern; IDs and accession numbers use the `TEST`, `SYN` and `SYNACC` prefixes.
* DICOM UIDs use the `2.25.` root followed by random digits (UUID-derived UIDs that were never issued to a study).
* IP addresses come from the documentation ranges in RFC 5737 (192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24);
  host names use the reserved `.example` and `example.org` domains.
* `vidacare-connector/VIDAcare.Connector.log` copies the layout of a VIDAcare Connector log (log4net lines, memory
  counter, thread names, A-ASSOCIATE details, C-FIND/C-ECHO PDUs and dataset dumps) with invented AE titles, addresses
  and values, staging each worklist inconsistency the analysis reports.
* `exam/Gateway-b3dweb-exam.log` copies the wording and line layout of a B3DWEB Gateway (C-FIND queries, C-MOVE
  retrievals, `Receiving` progress lines with `StUID`/`SeUID`, upload jobs, C-STORE sends with `Dest AET`), with every
  identifier, name, address and path invented.

The line layout follows the two formats observed in the sample archive (B3DWEB Gateway and DICOM Router).
Never replace these files with real logs, and never add real logs, screenshots of real logs or real identifiers
anywhere in this repository.
