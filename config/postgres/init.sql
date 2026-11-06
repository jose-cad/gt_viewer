-- Database initialization script for gt_viewer development environment
SELECT 'CREATE DATABASE meshcentral'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'meshcentral')\gexec

-- Create application user with password if not exists
DO
$do$
BEGIN
   IF NOT EXISTS (
      SELECT FROM pg_catalog.pg_roles
      WHERE  rolname = 'gtv_app') THEN
      CREATE ROLE gtv_app WITH LOGIN PASSWORD 'gtv_app_secret_pass_2026!';
   END IF;
END
$do$;

-- Create retention routine user with password if not exists
DO
$do$
BEGIN
   IF NOT EXISTS (
      SELECT FROM pg_catalog.pg_roles
      WHERE  rolname = 'gtv_retention') THEN
      CREATE ROLE gtv_retention WITH LOGIN PASSWORD 'gtv_retention_secret_pass_2026!';
   END IF;
END
$do$;
