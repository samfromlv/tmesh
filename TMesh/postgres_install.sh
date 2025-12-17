docker run -d \
  --name postgres \
  --restart unless-stopped \
  --network host \
  -e POSTGRES_USER='<your_postgres_username>' \
  -e POSTGRES_PASSWORD='<your_postgres_password>' \
  -e POSTGRES_DB=TBot \
  -v ~/postgres/data:/var/lib/postgresql/data \
  -v ~/postgres/logs:/var/log/postgresql \
  postgres:16 \
  postgres \
    -c listen_addresses='*' \
    -c shared_buffers=128MB \
    -c work_mem=4MB \
    -c maintenance_work_mem=32MB \
    -c max_connections=50 \
    -c wal_buffers=4MB \
    -c log_destination='stderr' \
    -c logging_collector=off
dbd54c5fd6ef4c1afd952e4e24d265c3d74be1ef8d2d872b398437956642a3eb