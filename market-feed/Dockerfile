FROM python:3.13-slim
WORKDIR /app
COPY requirements.txt ./
RUN pip install --no-cache-dir -r requirements.txt
COPY feed.py ./
ENV PYTHONUNBUFFERED=1 TQ_FEED_HOST=0.0.0.0
USER 10001:10001
EXPOSE 8765
CMD ["python", "feed.py"]
