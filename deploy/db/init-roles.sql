-- Least-privilege application role (ADR-0047 §3). Idempotent: safe to re-run on
-- every deploy, and it must be, because the AuditEvents revoke below needs the
-- table that only exists after migrations.
--
--   docker compose exec -T db psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" \
--     -v app_user="$APP_DB_USER" -v app_password="$APP_DB_PASSWORD" < deploy/db/init-roles.sql

-- psql does not interpolate :variables inside dollar-quoted bodies, so the two
-- values are handed to the DO blocks as session settings instead.
SELECT set_config('carnival.app_user', :'app_user', false) AS app_user;
SELECT set_config('carnival.app_password', :'app_password', false) IS NOT NULL AS password_loaded;

DO $$
DECLARE
  app_user     text := current_setting('carnival.app_user');
  app_password text := current_setting('carnival.app_password');
BEGIN
  IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = app_user) THEN
    EXECUTE format('ALTER ROLE %I WITH LOGIN PASSWORD %L', app_user, app_password);
  ELSE
    EXECUTE format('CREATE ROLE %I WITH LOGIN PASSWORD %L', app_user, app_password);
  END IF;
END
$$;

DO $$
DECLARE
  app_user text := current_setting('carnival.app_user');
  owner    text := current_user;
BEGIN
  EXECUTE format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), app_user);
  EXECUTE format('GRANT USAGE ON SCHEMA public TO %I', app_user);
  EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO %I', app_user);
  EXECUTE format('GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO %I', app_user);
  EXECUTE format('ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public '
                 'GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO %I', owner, app_user);
  EXECUTE format('ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public '
                 'GRANT USAGE, SELECT ON SEQUENCES TO %I', owner, app_user);
END
$$;

-- After the blanket grants, never before. The append-only trigger (assessment
-- C7) stays as the second lock.
DO $$
DECLARE
  app_user text := current_setting('carnival.app_user');
BEGIN
  IF EXISTS (SELECT 1 FROM pg_tables WHERE schemaname = 'public' AND tablename = 'AuditEvents') THEN
    EXECUTE format('REVOKE UPDATE, DELETE ON public."AuditEvents" FROM %I', app_user);
    RAISE NOTICE 'AuditEvents: UPDATE/DELETE revoked from %', app_user;
  ELSE
    RAISE NOTICE 'AuditEvents absent (pre-migration run); re-run after the API is healthy.';
  END IF;
END
$$;

REVOKE ALL ON SCHEMA public FROM PUBLIC;

DO $$
DECLARE
  app_user text := current_setting('carnival.app_user');
BEGIN
  EXECUTE format('ALTER ROLE %I SET statement_timeout = ''60s''', app_user);
  EXECUTE format('ALTER ROLE %I SET idle_in_transaction_session_timeout = ''60s''', app_user);
  EXECUTE format('ALTER ROLE %I SET lock_timeout = ''10s''', app_user);
END
$$;
