SELECT TOP 1 qo.quality_order_id AS qo_id, m.qo_material_id AS qo_mat_id
FROM   qms_quality_order qo
JOIN   qms_quality_order_material m ON m.quality_order_id = qo.quality_order_id
WHERE  qo.status_code IN ('Open','Reopened')
ORDER  BY qo.quality_order_id DESC, m.qo_material_id;
