#!/bin/bash
# Oracle fixture setup (Phase D4). The image runs scripts in /opt/oracle/scripts/setup once, after it creates the database,
# as the oracle OS user. Two accounts in FREEPDB1, passwords from compose.yml (fixture-only, never valid elsewhere):
#   querithm_app  least privileged: CREATE SESSION and objects in its own schema only (no ANY privilege, no catalog role).
#   querithm_mon  CREATE SESSION + SELECT_CATALOG_ROLE and the V$ views the monitor and plan sessions read (D4 S09, S12).
# The heredoc is unquoted so the passwords expand; every literal $ of a V$ view is escaped.
sqlplus -S -L / as sysdba <<SQL
WHENEVER SQLERROR EXIT FAILURE
ALTER SESSION SET CONTAINER = FREEPDB1;
CREATE USER querithm_app IDENTIFIED BY "${QUERITHM_APP_PASSWORD}" DEFAULT TABLESPACE USERS QUOTA 512M ON USERS;
GRANT CREATE SESSION, CREATE TABLE, CREATE VIEW, CREATE SEQUENCE, CREATE PROCEDURE, CREATE TRIGGER, CREATE TYPE,
      CREATE SYNONYM TO querithm_app;
CREATE USER querithm_mon IDENTIFIED BY "${QUERITHM_MON_PASSWORD}";
GRANT CREATE SESSION, SELECT_CATALOG_ROLE TO querithm_mon;
GRANT SELECT ON SYS.V_\$SESSION TO querithm_mon;
GRANT SELECT ON SYS.V_\$SQL TO querithm_mon;
GRANT SELECT ON SYS.V_\$SQL_PLAN TO querithm_mon;
GRANT SELECT ON SYS.V_\$SQL_PLAN_STATISTICS_ALL TO querithm_mon;
GRANT SELECT ON SYS.V_\$LOCK TO querithm_mon;
EXIT
SQL
