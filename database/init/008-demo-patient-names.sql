-- Rename only the five original demo records in an existing database.
UPDATE patients AS patient
SET display_name = seed.display_name
FROM (VALUES (1,'김서윤'), (2,'박도현'), (3,'이하린'), (4,'정민재'), (5,'최지우')) AS seed(n,display_name)
WHERE patient.id = ('30000000-0000-0000-0000-' || lpad(seed.n::text,12,'0'))::uuid
  AND patient.patient_number = 'P' || lpad(seed.n::text,4,'0')
  AND patient.display_name = '가상환자' || lpad(seed.n::text,2,'0');
