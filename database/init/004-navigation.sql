-- Apply explicitly to existing development volumes. Safe to re-run without resetting edited links.
BEGIN;
CREATE TABLE IF NOT EXISTS map_floors (
 id uuid PRIMARY KEY, code text NOT NULL UNIQUE, name text NOT NULL,
 width double precision NOT NULL CHECK(width>0 AND width<'Infinity'::float8),
 height double precision NOT NULL CHECK(height>0 AND height<'Infinity'::float8)
);
CREATE TABLE IF NOT EXISTS map_nodes (
 id uuid PRIMARY KEY, floor_id uuid NOT NULL REFERENCES map_floors(id), name text NOT NULL,
 kind text NOT NULL CHECK(kind IN ('Entrance','Kiosk','Corridor','Elevator','Room')),
 x double precision NOT NULL CHECK(x>=0 AND x<'Infinity'::float8),
 y double precision NOT NULL CHECK(y>=0 AND y<'Infinity'::float8), UNIQUE(id,floor_id)
);
CREATE TABLE IF NOT EXISTS map_links (
 id uuid PRIMARY KEY, floor_id uuid NOT NULL REFERENCES map_floors(id),
 from_node_id uuid NOT NULL, to_node_id uuid NOT NULL,
 distance_meters double precision NOT NULL CHECK(distance_meters>0 AND distance_meters<'Infinity'::float8),
 weight double precision NOT NULL CHECK(weight>0 AND weight<'Infinity'::float8),
 is_closed boolean NOT NULL DEFAULT false, is_bidirectional boolean NOT NULL DEFAULT true,
 FOREIGN KEY(from_node_id,floor_id) REFERENCES map_nodes(id,floor_id),
 FOREIGN KEY(to_node_id,floor_id) REFERENCES map_nodes(id,floor_id),
 CHECK(from_node_id<>to_node_id), UNIQUE(floor_id,from_node_id,to_node_id)
);
CREATE TABLE IF NOT EXISTS map_zones (
 id uuid PRIMARY KEY, floor_id uuid NOT NULL REFERENCES map_floors(id), name text NOT NULL,
 kind text NOT NULL CHECK(kind IN ('Clinic','Waiting','Reception','Facility')),
 x double precision NOT NULL CHECK(x>=0 AND x<'Infinity'::float8),
 y double precision NOT NULL CHECK(y>=0 AND y<'Infinity'::float8),
 width double precision NOT NULL CHECK(width>0 AND width<'Infinity'::float8),
 height double precision NOT NULL CHECK(height>0 AND height<'Infinity'::float8)
);
CREATE TABLE IF NOT EXISTS room_map_destinations (
 room_id uuid PRIMARY KEY REFERENCES rooms(id), node_id uuid NOT NULL UNIQUE REFERENCES map_nodes(id)
);
INSERT INTO map_floors VALUES ('60000000-0000-0000-0000-000000000001','F1','MEDI-WAY 병원 1층',600,600) ON CONFLICT DO NOTHING;
INSERT INTO map_nodes(id,floor_id,name,kind,x,y)
SELECT ('61000000-0000-0000-0000-'||lpad(n::text,12,'0'))::uuid,
 '60000000-0000-0000-0000-000000000001',name,kind,x,y FROM (VALUES
 (1,'정문','Entrance',100,550),(2,'접수 키오스크','Kiosk',100,480),
 (3,'서쪽 아래 복도','Corridor',100,420),(4,'중앙 아래 복도','Corridor',300,420),
 (5,'동쪽 아래 복도','Corridor',500,420),(6,'서쪽 위 복도','Corridor',100,180),
 (7,'중앙 위 복도','Corridor',300,180),(8,'동쪽 위 복도','Corridor',500,180),
 (9,'내과 1진료실 입구','Room',300,100),(10,'내과 2진료실 입구','Room',500,100),
 (11,'엘리베이터','Elevator',100,100),(12,'중앙 복도','Corridor',300,300)
) AS nodes(n,name,kind,x,y) ON CONFLICT DO NOTHING;
INSERT INTO map_links(id,floor_id,from_node_id,to_node_id,distance_meters,weight)
SELECT ('62000000-0000-0000-0000-'||lpad(n::text,12,'0'))::uuid,
 '60000000-0000-0000-0000-000000000001',
 ('61000000-0000-0000-0000-'||lpad(a::text,12,'0'))::uuid,
 ('61000000-0000-0000-0000-'||lpad(b::text,12,'0'))::uuid,meters,cost FROM (VALUES
 (1,1,2,7,7),(2,2,3,6,6),(3,3,4,20,20),(4,4,5,20,20),
 (5,3,6,24,30),(6,6,7,20,20),(7,7,8,20,20),(8,5,8,24,24),
 (9,7,9,8,8),(10,8,10,8,8),(11,6,11,8,8),(12,4,12,12,12),(13,12,7,12,12)
) AS links(n,a,b,meters,cost) ON CONFLICT DO NOTHING;
INSERT INTO map_zones(id,floor_id,name,kind,x,y,width,height)
SELECT ('63000000-0000-0000-0000-'||lpad(n::text,12,'0'))::uuid,
 '60000000-0000-0000-0000-000000000001',name,kind,x,y,w,h FROM (VALUES
 (1,'내과 1진료실','Clinic',230,20,140,65),(2,'내과 2진료실','Clinic',430,20,140,65),
 (3,'엘리베이터','Facility',20,20,140,65),(4,'대기실','Waiting',150,220,110,140),
 (5,'접수 안내','Reception',180,470,240,90),(6,'화장실','Facility',350,250,80,100)
) AS zones(n,name,kind,x,y,w,h) ON CONFLICT DO NOTHING;
INSERT INTO room_map_destinations VALUES
 ('20000000-0000-0000-0000-000000000001','61000000-0000-0000-0000-000000000009'),
 ('20000000-0000-0000-0000-000000000002','61000000-0000-0000-0000-000000000010')
ON CONFLICT DO NOTHING;
COMMIT;
