from django.contrib import admin
from django.urls import include, path

from monitoring import views

urlpatterns = [
    path("", views.dashboard, name="dashboard"),
    path("sw.js", views.service_worker, name="service-worker"),
    path("admin/", admin.site.urls),
    path("api/", include("monitoring.urls")),
]
