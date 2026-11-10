#!/bin/bash
# Oracle fixture setup (Phase D4). The image runs scripts in /opt/oracle/scripts/startup on every start, as the oracle OS
# user (its setup folder runs only when the image creates a database, and the Free image ships one already created), so
# this is idempotent: each account is created when missing and its password set to the compose.yml value either way. Two accounts in FREEPDB1, passwords from compose.yml (fixture-only, never valid elsewhere):
#   querithm_app  least privileged: CREATE SESSION and objects in its own schema only (no ANY privilege, no catalog role).
#   querithm_mon  CREATE SESSION + SELECT_CATALOG_ROLE and the V$ views the monitor and plan sessions read (D4 S09, S12).
# The heredoc is unquoted so the passwords expand; every literal $ of a V$ view is escaped.
sqlplus -S -L / as sysdba <<SQL
WHENEVER SQLERROR EXIT FAILURE
ALTER SESSION SET CONTAINER = FREEPDB1;
DECLARE
  PROCEDURE ensure(name VARCHAR2, pw VARCHAR2, extra VARCHAR2) IS
    n NUMBER;
  BEGIN
    SELECT COUNT(*) INTO n FROM dba_users WHERE username = UPPER(name);
    IF n = 0 THEN
      EXECUTE IMMEDIATE 'CREATE USER ' || name || ' IDENTIFIED BY "' || pw || '"' || extra;
    ELSE
      EXECUTE IMMEDIATE 'ALTER USER ' || name || ' IDENTIFIED BY "' || pw || '" ACCOUNT UNLOCK';
    END IF;
  END;
BEGIN
  ensure('querithm_app', '${QUERITHM_APP_PASSWORD}', ' DEFAULT TABLESPACE USERS QUOTA 512M ON USERS');
  ensure('querithm_mon', '${QUERITHM_MON_PASSWORD}', '');
END;
/
GRANT CREATE SESSION, CREATE TABLE, CREATE VIEW, CREATE SEQUENCE, CREATE PROCEDURE, CREATE TRIGGER, CREATE TYPE,
      CREATE SYNONYM TO querithm_app;
GRANT CREATE SESSION, SELECT_CATALOG_ROLE TO querithm_mon;
GRANT SELECT ON SYS.V_\$SESSION TO querithm_mon;
GRANT SELECT ON SYS.V_\$SQL TO querithm_mon;
GRANT SELECT ON SYS.V_\$SQL_PLAN TO querithm_mon;
GRANT SELECT ON SYS.V_\$SQL_PLAN_STATISTICS_ALL TO querithm_mon;
GRANT SELECT ON SYS.V_\$LOCK TO querithm_mon;
EXIT
SQL
