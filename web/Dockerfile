# syntax=docker/dockerfile:1
#
#   docker build -t helpdeskflow/web web
#
# Estágio 1: compila o React (Node). Estágio 2: só o resultado estático, servido pelo nginx sem root.

FROM node:22-alpine AS build
WORKDIR /app

# Dependências primeiro: aproveita o cache do Docker quando só o código muda.
COPY package.json package-lock.json ./
RUN npm ci

COPY . .
RUN npm run build

# nginx "unprivileged": roda como usuário comum e escuta na 8080.
FROM nginxinc/nginx-unprivileged:1.29-alpine AS runtime
# O nginx.conf é um TEMPLATE: a imagem oficial substitui ${NGINX_...} por variáveis de ambiente ao iniciar.
# (NGINX_ENVSUBST_FILTER limita a substituição a elas, para nunca tocar nas variáveis do próprio nginx, como $uri.)
COPY nginx.conf /etc/nginx/templates/default.conf.template
# NGINX_ENTRYPOINT_LOCAL_RESOLVERS=1 faz a imagem preencher NGINX_LOCAL_RESOLVERS com o DNS do /etc/resolv.conf
# (Docker: 127.0.0.11; Kubernetes: o CoreDNS), então o mesmo arquivo funciona nos dois ambientes.
ENV NGINX_ENVSUBST_FILTER=NGINX_ \
    NGINX_ENTRYPOINT_LOCAL_RESOLVERS=1 \
    NGINX_GATEWAY_URL=http://gateway:8080
COPY security-headers.conf /etc/nginx/security-headers.conf
COPY real-ip-default.conf /etc/nginx/real-ip.d/00-default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 8080
