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
-- D4 S05: querithm_own, a schema-only account (NO AUTHENTICATION: nobody signs in as it) owning objects the other two may
-- use but not define: a synonym into another schema, the schema list, GET_DDL refused (ORA-31603) for a procedure the
-- least-privileged user may only execute, and allowed for the catalog-role user.
DECLARE
  n NUMBER;
BEGIN
  SELECT COUNT(*) INTO n FROM dba_users WHERE username = 'QUERITHM_OWN';
  IF n = 0 THEN
    EXECUTE IMMEDIATE 'CREATE USER querithm_own NO AUTHENTICATION DEFAULT TABLESPACE USERS QUOTA 16M ON USERS';
  END IF;
  SELECT COUNT(*) INTO n FROM dba_objects WHERE owner = 'QUERITHM_OWN' AND object_name = 'SHARED_T';
  IF n = 0 THEN
    EXECUTE IMMEDIATE 'CREATE TABLE querithm_own.shared_t (id NUMBER PRIMARY KEY, label VARCHAR2(40))';
  END IF;
  EXECUTE IMMEDIATE 'CREATE OR REPLACE PROCEDURE querithm_own.shared_p (p_id IN NUMBER) AS BEGIN NULL; END;';
  EXECUTE IMMEDIATE 'CREATE OR REPLACE VIEW querithm_own.shared_v AS SELECT id, label FROM querithm_own.shared_t';
  EXECUTE IMMEDIATE 'GRANT SELECT ON querithm_own.shared_t TO querithm_app, querithm_mon';
  EXECUTE IMMEDIATE 'GRANT SELECT ON querithm_own.shared_v TO querithm_app';
  EXECUTE IMMEDIATE 'GRANT EXECUTE ON querithm_own.shared_p TO querithm_app, querithm_mon';
  -- D4 S08: a package with a body and an overloaded member. EXECUTE alone (querithm_app) shows the specification only;
  -- DEBUG as well (querithm_mon) shows the body too.
  EXECUTE IMMEDIATE 'CREATE OR REPLACE PACKAGE querithm_own.shared_pkg AS PROCEDURE p(a NUMBER); PROCEDURE p(a VARCHAR2); END;';
  EXECUTE IMMEDIATE 'CREATE OR REPLACE PACKAGE BODY querithm_own.shared_pkg AS PROCEDURE p(a NUMBER) IS BEGIN NULL; END; PROCEDURE p(a VARCHAR2) IS BEGIN NULL; END; END;';
  EXECUTE IMMEDIATE 'GRANT EXECUTE ON querithm_own.shared_pkg TO querithm_app, querithm_mon';
  EXECUTE IMMEDIATE 'GRANT DEBUG ON querithm_own.shared_pkg TO querithm_mon';
END;
/
-- D4 S05: the metadata tests create a materialized view and an external table (its directory is never read: no file).
GRANT CREATE MATERIALIZED VIEW TO querithm_app;
CREATE OR REPLACE DIRECTORY querithm_ext AS '/tmp/querithm-ext';
GRANT READ ON DIRECTORY querithm_ext TO querithm_app;
EXIT
SQL
