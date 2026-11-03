BEGIN;
ALTER TABLE visits ADD COLUMN IF NOT EXISTS queue_priority bigint NOT NULL DEFAULT 0 CHECK (queue_priority >= 0);
CREATE INDEX IF NOT EXISTS ix_visits_waiting_order
    ON visits(room_id, service_date, queue_priority DESC, queue_number) WHERE status='Waiting';
CREATE TABLE IF NOT EXISTS queue_order_requests (
    request_id uuid PRIMARY KEY,
    room_id uuid NOT NULL REFERENCES rooms(id),
    service_date date NOT NULL,
    fingerprint text NOT NULL CHECK (length(fingerprint)=64),
    created_at timestamptz NOT NULL DEFAULT now()
);
COMMIT;
