-- Existing databases: apply explicitly; init scripts run automatically only on first startup.
BEGIN;
CREATE TABLE IF NOT EXISTS visit_requests (
 request_id uuid PRIMARY KEY,
 request_fingerprint text NOT NULL,
 visit_id uuid NOT NULL REFERENCES visits(id),
 created_at timestamptz NOT NULL DEFAULT now()
);
INSERT INTO visit_requests(request_id,request_fingerprint,visit_id)
 SELECT request_id,request_fingerprint,id FROM visits
 ON CONFLICT(request_id) DO NOTHING;
COMMIT;
