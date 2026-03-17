FROM node:20-slim
WORKDIR /app
COPY package.json server.js ./
RUN npm install --omit=dev ws
EXPOSE 8080
ENV PORT=8080
CMD ["node", "server.js"]
