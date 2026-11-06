-- Database initialization script for gt_viewer development environment
SELECT 'CREATE DATABASE meshcentral'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'meshcentral')\gexec
