#!/bin/bash
# Oracle fixture TCPS (Phase D4, owner decision O2). The Free image ships a database it already created, so its own
# ENABLE_TCPS step (run only right after creating one) never runs: this startup script runs the image's configTcps.sh
# once instead, on 2484, with a self-signed certificate for CN=localhost (the name the tests dial). The image's
# HOSTNAME would be the container id, so the name is passed explicitly. Re-runs are skipped once the listener has TCPS.
set -e
if grep -qi "PROTOCOL *= *TCPS" "$ORACLE_HOME/network/admin/listener.ora" 2>/dev/null \
   || grep -qi "PROTOCOL *= *TCPS" "$ORACLE_BASE/oradata/dbconfig/$ORACLE_SID/listener.ora" 2>/dev/null; then
  echo "querithm: TCPS already configured"
  exit 0
fi
TCPS_PORT=2484 "$ORACLE_BASE/scripts/base/configTcps.sh" 2484 localhost
