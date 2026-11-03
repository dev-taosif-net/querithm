# PostgreSQL 16 with the pldebugger extension (§11 PostgreSQL debugger) and pg_wait_sampling
# (Phase B activity monitor: in-server wait profile). pg_stat_statements ships with the server.
FROM postgres:16
RUN apt-get update \
 && apt-get install -y --no-install-recommends postgresql-16-pldebugger postgresql-16-pg-wait-sampling \
 && rm -rf /var/lib/apt/lists/*
# max_connections ≥ 600: the activity-monitor bench opens 500 idle sessions (§14 metric 28).
CMD ["postgres", "-c", "shared_preload_libraries=plugin_debugger,pg_stat_statements,pg_wait_sampling", "-c", "max_connections=600", "-c", "track_io_timing=on"]
