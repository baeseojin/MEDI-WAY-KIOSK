BEGIN;
CREATE TABLE patients (id uuid PRIMARY KEY, patient_number text NOT NULL UNIQUE, display_name text NOT NULL);
CREATE TABLE departments (id uuid PRIMARY KEY, code text NOT NULL UNIQUE, name text NOT NULL);
CREATE TABLE rooms (
 id uuid PRIMARY KEY, department_id uuid NOT NULL REFERENCES departments(id),
 code text NOT NULL UNIQUE, name text NOT NULL, UNIQUE(id, department_id)
);
CREATE TABLE visits (
 id uuid PRIMARY KEY, patient_id uuid NOT NULL REFERENCES patients(id),
 department_id uuid NOT NULL REFERENCES departments(id), room_id uuid NOT NULL,
 service_date date NOT NULL, queue_number integer NOT NULL CHECK(queue_number > 0),
 status text NOT NULL CHECK(status IN ('Waiting','Called','InService','Completed','Cancelled')),
 request_id uuid NOT NULL UNIQUE, request_fingerprint text NOT NULL,
 version integer NOT NULL DEFAULT 1 CHECK(version > 0),
 created_at timestamptz NOT NULL DEFAULT now(), called_at timestamptz,
 started_at timestamptz, completed_at timestamptz, cancelled_at timestamptz,
 FOREIGN KEY(room_id, department_id) REFERENCES rooms(id, department_id),
 UNIQUE(department_id, service_date, queue_number)
);
CREATE UNIQUE INDEX ux_visits_active_patient ON visits(patient_id,department_id,service_date)
 WHERE status IN ('Waiting','Called','InService');
CREATE UNIQUE INDEX ux_visits_current_room ON visits(room_id) WHERE status IN ('Called','InService');
CREATE INDEX ix_visits_queue ON visits(room_id,service_date,queue_number) WHERE status='Waiting';
COMMIT;
