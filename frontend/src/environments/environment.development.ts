/** `ng serve`: the ASP.NET Core API runs separately on port 5291 (CORS allows http://localhost:4200). */
export const environment = {
  production: false,
  apiBaseUrl: 'http://localhost:5291',
  hubUrl: 'http://localhost:5291/hubs/monitoring',
};
