# ---- Stage 1: build the catalogue ----
FROM node:24-alpine AS build
WORKDIR /app

COPY package*.json ./
RUN npm ci

COPY . .
RUN npm run build

# ---- Stage 2: hand out the catalogue ----
FROM nginx:alpine AS runtime

COPY --from=build /app/dist /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf

RUN chown -R 101:101 /var/cache/nginx /etc/nginx/conf.d \
 && mkdir -p /tmp/nginx && chown 101:101 /tmp/nginx \
 && sed -i 's|^pid .*|pid /tmp/nginx/nginx.pid;|' /etc/nginx/nginx.conf

EXPOSE 8080
USER 101