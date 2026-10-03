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
COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY security-headers.conf /etc/nginx/security-headers.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 8080
