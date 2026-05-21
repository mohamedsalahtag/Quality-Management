SELECT
  (SELECT ConfigValue FROM SiteConfiguration WHERE ConfigKey='Sap.Url.ContainerSearch')         AS container_url,
  (SELECT ConfigValue FROM SiteConfiguration WHERE ConfigKey='Sap.Url.ContainerSearch.User')    AS container_user,
  CASE WHEN EXISTS (SELECT 1 FROM SiteConfiguration WHERE ConfigKey='Sap.Url.ContainerSearch.Password' AND ConfigValue <> '') THEN 'yes' ELSE 'no' END AS container_pwd_saved,
  (SELECT ConfigValue FROM SiteConfiguration WHERE ConfigKey='Sap.User')                        AS global_user,
  CASE WHEN EXISTS (SELECT 1 FROM SiteConfiguration WHERE ConfigKey='Sap.Password' AND ConfigValue <> '') THEN 'yes' ELSE 'no' END AS global_pwd_saved;
