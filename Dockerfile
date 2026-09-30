FROM python:3.12-slim

ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1

WORKDIR /app

COPY requirements.txt .
RUN pip install -r requirements.txt "psycopg[binary]>=3.1"

COPY . .
RUN DJANGO_DEBUG=False DJANGO_SECRET_KEY=build python manage.py collectstatic --noinput

RUN useradd --create-home sentinel && chown -R sentinel /app
USER sentinel

EXPOSE 8000
CMD ["sh", "-c", "python manage.py migrate --noinput && daphne -b 0.0.0.0 -p 8000 --proxy-headers sentinel.asgi:application"]
