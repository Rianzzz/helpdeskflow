-- Cada microsserviço tem o SEU banco: ninguém lê tabelas de outro serviço, só conversa pela API/eventos.
-- (Roda só na primeira criação do volume. Em volume existente: docker exec helpdesk-postgres createdb -U helpdesk <nome>)
CREATE DATABASE helpdesk_tickets;
CREATE DATABASE helpdesk_identity;
