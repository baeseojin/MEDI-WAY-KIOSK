-- Existing duplicates cause rollback; never silently cancel a patient's visit.
BEGIN;
CREATE UNIQUE INDEX IF NOT EXISTS ux_visits_active_patient_hospital
 ON visits(patient_id,service_date) WHERE status IN ('Waiting','Called','InService');
DROP INDEX IF EXISTS ux_visits_active_patient;
COMMIT;
