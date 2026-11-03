BEGIN;
INSERT INTO map_floors VALUES ('60000000-0000-0000-0000-000000000002','F2','MEDI-WAY 병원 2층',600,600) ON CONFLICT DO NOTHING;
ALTER TABLE rooms ADD COLUMN IF NOT EXISTS floor_id uuid REFERENCES map_floors(id);
UPDATE rooms SET floor_id='60000000-0000-0000-0000-000000000001'
 WHERE id IN ('20000000-0000-0000-0000-000000000001','20000000-0000-0000-0000-000000000002') AND floor_id IS NULL;
INSERT INTO departments VALUES ('10000000-0000-0000-0000-000000000002','SURGERY','외과') ON CONFLICT DO NOTHING;
INSERT INTO rooms(id,department_id,code,name,floor_id) VALUES
 ('20000000-0000-0000-0000-000000000003','10000000-0000-0000-0000-000000000002','SU03','외과 3진료실','60000000-0000-0000-0000-000000000002'),
 ('20000000-0000-0000-0000-000000000004','10000000-0000-0000-0000-000000000002','SU04','외과 4진료실','60000000-0000-0000-0000-000000000002') ON CONFLICT DO NOTHING;
INSERT INTO map_nodes(id,floor_id,name,kind,x,y)
SELECT ('64000000-0000-0000-0000-'||lpad(n::text,12,'0'))::uuid,
 '60000000-0000-0000-0000-000000000002',name,kind,x,y FROM (VALUES
 (1,'2층 엘리베이터','Elevator',100,100),(2,'2층 서쪽 복도','Corridor',100,300),
 (3,'2층 중앙 복도','Corridor',300,300),(4,'외과 3진료실 입구','Room',300,100),
 (5,'2층 동쪽 복도','Corridor',500,300),(6,'외과 4진료실 입구','Room',500,100)
) AS nodes(n,name,kind,x,y) ON CONFLICT DO NOTHING;
INSERT INTO map_links(id,floor_id,from_node_id,to_node_id,distance_meters,weight)
SELECT ('65000000-0000-0000-0000-'||lpad(n::text,12,'0'))::uuid,
 '60000000-0000-0000-0000-000000000002',
 ('64000000-0000-0000-0000-'||lpad(a::text,12,'0'))::uuid,
 ('64000000-0000-0000-0000-'||lpad(b::text,12,'0'))::uuid,20,20 FROM (VALUES
 (1,1,2),(2,2,3),(3,3,4),(4,3,5),(5,5,6)
) AS links(n,a,b) ON CONFLICT DO NOTHING;
INSERT INTO map_zones(id,floor_id,name,kind,x,y,width,height)
SELECT ('66000000-0000-0000-0000-'||lpad(n::text,12,'0'))::uuid,
 '60000000-0000-0000-0000-000000000002',name,kind,x,y,w,h FROM (VALUES
 (1,'외과 3진료실','Clinic',230,20,140,65),(2,'외과 4진료실','Clinic',430,20,140,65),
 (3,'엘리베이터','Facility',20,20,140,65),(4,'외과 대기실','Waiting',180,400,240,100)
) AS zones(n,name,kind,x,y,w,h) ON CONFLICT DO NOTHING;
INSERT INTO room_map_destinations VALUES
 ('20000000-0000-0000-0000-000000000003','64000000-0000-0000-0000-000000000004'),
 ('20000000-0000-0000-0000-000000000004','64000000-0000-0000-0000-000000000006') ON CONFLICT DO NOTHING;
CREATE TABLE IF NOT EXISTS map_floor_connections (
 id uuid PRIMARY KEY,from_node_id uuid NOT NULL REFERENCES map_nodes(id),to_node_id uuid NOT NULL REFERENCES map_nodes(id),
 distance_meters double precision NOT NULL CHECK(distance_meters>0 AND distance_meters<'Infinity'::float8),
 weight double precision NOT NULL CHECK(weight>0 AND weight<'Infinity'::float8),
 is_closed boolean NOT NULL DEFAULT false,is_bidirectional boolean NOT NULL DEFAULT true,
 CHECK(from_node_id<>to_node_id),UNIQUE(from_node_id,to_node_id)
);
INSERT INTO map_floor_connections VALUES
 ('67000000-0000-0000-0000-000000000001','61000000-0000-0000-0000-000000000011','64000000-0000-0000-0000-000000000001',5,10,false,true)
 ON CONFLICT DO NOTHING;
COMMIT;
