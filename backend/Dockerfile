FROM python:3.11-slim

WORKDIR /app

# Install system dependencies
RUN apt-get update && apt-get install -y \
    wireguard-tools \
    iptables \
    iproute2 \
    net-tools \
    openresolv \
    procps \
    && rm -rf /var/lib/apt/lists/*

# Copy requirements first for better caching
COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

# Copy application code
COPY . .

# Expose API port
EXPOSE 51507

# Run the application
CMD ["uvicorn", "main:app", "--host", "0.0.0.0", "--port", "51507", "--reload"]
