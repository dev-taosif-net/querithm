#!/bin/bash
# Oracle fixture healthcheck (Phase D4): healthy only when FREEPDB1 is open AND setup/01-users.sh has created the test
# user, because it signs in as that user. The password goes to sqlplus on stdin, never on a command line.
out=$(sqlplus -S -L /nolog <<SQL
WHENEVER SQLERROR EXIT FAILURE
CONNECT querithm_app/"${QUERITHM_APP_PASSWORD}"@//localhost:1521/FREEPDB1
SET HEADING OFF FEEDBACK OFF PAGESIZE 0
SELECT 'querithm-ready' FROM DUAL;
EXIT
SQL
) || exit 1
[[ "$out" == *querithm-ready* ]]
