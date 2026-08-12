#!/usr/bin/env bash
set -euo pipefail

readonly backup_file="/var/opt/mssql/backup/gtk_2026-07-30.bak"
readonly sqlcmd_path="/opt/mssql-tools18/bin/sqlcmd"

if [[ ! -r "$backup_file" ]]; then
  echo "Required backup is not readable: $backup_file" >&2
  exit 1
fi

if [[ ! "${ALTA_DB_PASSWORD:-}" =~ ^[A-Za-z0-9_@%+=.!,:/-]{16,128}$ ]]; then
  echo "ALTA_DB_PASSWORD must be 16-128 characters using the supported development-safe character set." >&2
  exit 1
fi

run_sqlcmd() {
  SQLCMDPASSWORD="$MSSQL_SA_PASSWORD" "$sqlcmd_path" \
    -S mssql -U sa -C -b -V 16 "$@"
}

echo "Verifying gtk backup."
run_sqlcmd -Q "RESTORE VERIFYONLY FROM DISK = N'$backup_file';"

file_list="$(run_sqlcmd -h -1 -W -Q "RESTORE FILELISTONLY FROM DISK = N'$backup_file';")"
if ! grep -Eq '^gtk[[:space:]]' <<<"$file_list"; then
  echo "Backup does not contain the expected logical data file 'gtk'." >&2
  exit 1
fi
if ! grep -Eq '^gtk_log[[:space:]]' <<<"$file_list"; then
  echo "Backup does not contain the expected logical log file 'gtk_log'." >&2
  exit 1
fi

database_exists="$(run_sqlcmd -h -1 -W -Q "SET NOCOUNT ON; SELECT CASE WHEN DB_ID(N'gtk') IS NULL THEN 0 ELSE 1 END;" | tr -d '[:space:]')"
if [[ "$database_exists" == "0" ]]; then
  echo "Restoring gtk database."
  run_sqlcmd -Q "
    RESTORE DATABASE [gtk]
      FROM DISK = N'$backup_file'
      WITH MOVE N'gtk' TO N'/var/opt/mssql/data/gtk.mdf',
           MOVE N'gtk_log' TO N'/var/opt/mssql/data/gtk_log.ldf',
           RECOVERY,
           STATS = 5;"
else
  echo "Database gtk already exists; restore skipped."
fi

echo "Waiting for gtk database recovery to complete."
database_state=""
for attempt in {1..60}; do
  database_state="$(run_sqlcmd -h -1 -W -Q "SET NOCOUNT ON; SELECT CONVERT(nvarchar(60), DATABASEPROPERTYEX(N'gtk', N'Status'));" | tr -d '[:space:]')"
  if [[ "$database_state" == "ONLINE" ]]; then
    break
  fi
  sleep 2
done
if [[ "$database_state" != "ONLINE" ]]; then
  echo "Database gtk did not become ONLINE within 120 seconds (last state: ${database_state:-unknown})." >&2
  exit 1
fi

echo "Ensuring least-privilege adapter login and database user."
run_sqlcmd -d master -v "AltaPassword=$ALTA_DB_PASSWORD" <<'SQL'
IF SUSER_ID(N'alta_adapter') IS NULL
BEGIN
    CREATE LOGIN [alta_adapter]
        WITH PASSWORD = N'$(AltaPassword)',
             CHECK_POLICY = ON,
             CHECK_EXPIRATION = OFF;
END;

IF ISNULL(IS_SRVROLEMEMBER(N'sysadmin', N'alta_adapter'), 1) <> 0
    THROW 51000, 'alta_adapter must not be a sysadmin.', 1;
SQL

run_sqlcmd -d gtk <<'SQL'
IF USER_ID(N'alta_adapter') IS NULL
    CREATE USER [alta_adapter] FOR LOGIN [alta_adapter];

GRANT CONNECT TO [alta_adapter];

IF ISNULL(IS_ROLEMEMBER(N'db_datareader', N'alta_adapter'), 0) <> 1
    ALTER ROLE [db_datareader] ADD MEMBER [alta_adapter];
SQL

echo "gtk development database is ready."
