---
app_name: "Sharbatly Quality Management System"
version: "1.0"
purpose: "A simple introduction to the new QMS — what it does, how the work flows, and the key features. Replaces QC One."
audiences: ["Operators", "Supervisors", "Managers", "Site administrators"]
site_url: "http://192.168.3.17:5244"
base_url: "http://192.168.3.17:5244"
roles: ["Viewer", "Operator", "Supervisor", "Manager", "ClaimManager", "SiteAdmin"]
theme: "default"
formats: ["pptx"]
lang: "en"
direction: "ltr"
login:
  url: "/Account/Login"
  username_field: "Username"
  password_field: "Password"
  submit_text: "Sign in"
---

## 1. Introduction

### 1.1 What is the new system?
- One place for all fruit-quality inspection.
- Follows the shipment: Arrival → Quality Order → Inspection → Report.
- Opens in any web browser on the company network.
- Used by inspectors, supervisors, managers.
- **Screenshot:** `screenshots/home-index.png`

### 1.2 It replaces QC One
- Live container data from SAP — no re-typing.
- Access controlled per person.
- Every change is recorded.
- Live dashboards instead of spreadsheets.
> **For Admins** — QC One is retired. Data now lives in the shared company database (Sharbatly_MIS); access is resolved centrally, so a change applies to everyone on their next action.

## 2. Objectives & Benefits

### 2.1 Why we built it
- Standardise how quality is inspected.
- Replace paper checklists and QC One.
- One source of truth for every shipment.
- Start inspection from real SAP data.

### 2.2 What you gain
- Faster inspections.
- Cleaner, comparable defect data.
- Full traceability.
- Real-time visibility.
> **For Admins** — Benefits scale by configuration, not code: new plants, roles, defects and units are all set up in the app.

## 3. How It Works

### 3.1 The big picture
- Sign in with your company account.
- Pick a container, check it in, inspect it, produce a report.
- Results flow into dashboards and reports.
- **Screenshot:** `screenshots/home-index.png`

### 3.2 The flow, step by step
- **1** — Pending Containers (from SAP)
- **2** — Arrival & Checklist
- **3** — Quality Order
- **4** — Inspection
- **5** — Final Report
> **For Admins** — Containers, materials and vendors sync from SAP (OData). Each step writes to the audit log. Hosted as a Windows service on the network (port 5244), ASP.NET Core + SQL Server, sign-in via Active Directory.

## 4. User Levels

### 4.1 The roles
- **Viewer** — look and download only.
- **Operator** — records inspections.
- **Supervisor** — reviews and finishes orders.
- **Manager** — full oversight; can reopen.
- **Claim Manager** — handles claims.

### 4.2 Who can do what
- Viewer: read screens, download the report.
- Operator: create arrivals, inspect, submit.
- Supervisor: finish, or send back for fixes.
- Manager: reopen a finished order.
- **Screenshot:** `screenshots/qualityorders-details.png`
> **For Admins** — Access is built from permissions, not fixed titles. Each screen is No access / Read only / Edit; each button is its own permission. Compose custom roles on the Security screen; changes take effect immediately. The built-in administrator can never lose the Security screen.

## 5. Branches & Access

### 5.1 Branch access
- Some roles are tied to one branch (plant).
- You see only your branch's shipments.
- Managers and admins see all branches.

### 5.2 Signing in
- Use your normal company username and password.
- You only see what your role allows.
- **Screenshot:** `screenshots/account-login.png`
> **For Admins** — Plant is assigned per user; unscoped roles see every plant. Hidden buttons are also blocked server-side. Admins can preview the app "as" another role.

## 6. The Modules

### 6.1 Dashboard
- Live overview of workload and status.
- **Screenshot:** `screenshots/home-index.png`

### 6.2 Arrivals
- Pending containers, arrivals, and checklists.
- **Screenshot:** `screenshots/arrivals-index.png`

### 6.3 Quality Orders
- Materials, samples, readings, defects, photos, report.
- **Screenshot:** `screenshots/qualityorders-index.png`

### 6.4 Claim Management
- Raise, review and decide quality claims.
- **Screenshot:** `screenshots/claimmanagement-index.png`

### 6.5 Reports
- Filter, pivot and export inspection results.
- **Screenshot:** `screenshots/reports-flatdefects.png`
> **For Admins** — Parameters (defect catalogue, reading types, sample headers, units, mail templates), Users, Security and Site Configuration are all under Administration.

## 7. Shipment Workflow

### 7.1 Step 1 — Pending Containers
- Containers due to arrive, listed from SAP.
- Pick one to start.
- **Screenshot:** `screenshots/arrivals-pending.png`

### 7.2 Step 2 — Arrival & Checklist
- Check the shipment in.
- Complete the checklist.
- **Screenshot:** `screenshots/arrivals-details.png`

### 7.3 Step 3 — Quality Order
- Open an order for the arrival.
- Add its materials.
- **Screenshot:** `screenshots/qualityorders-index.png`

### 7.4 Step 4 — Inspection
- Add samples and readings.
- Log defects; attach photos.
- **Screenshot:** `screenshots/qualityorders-sample.png`

### 7.5 Step 5 — Final Report
- Submit, then finish (approve).
- Download or email the PDF report.
- **Screenshot:** `screenshots/qualityorders-details.png`

## 8. Inspection & Approval

### 8.1 Recording the inspection
- Add a sample per what you examine.
- Enter the readings.
- Pick defects from the list; add photos.
- **Screenshot:** `screenshots/qualityorders-sample.png`

### 8.2 Getting it approved
- **Open** → you record the work.
- **Submitted** → you send it for review.
- **Finished** → the supervisor approves.
- Need a fix? The supervisor sends it back.
> **For Admins** — Status: Open → Submitted → Closed(Finished). Cancel-submit returns to Open; Manager can reopen a finished order. Report recipients are the mail groups set in Site Configuration.

### 8.3 The report
- A clean PDF of results, defects and photos.
- Can be emailed to the right people.

## 9. Dashboard & Reports

### 9.1 Dashboard
- See current status at a glance.
- Spot where attention is needed.
- **Screenshot:** `screenshots/home-index.png`

### 9.2 Reports
- One filterable table of all results.
- Pivot to compare plants, vendors, periods.
- Build tailored reports.
- Export to Excel and PDF.
- **Screenshot:** `screenshots/reports-flatdefects.png`

## 10. Best Practices

### 10.1 For inspectors
- Start from the real container.
- Finish the checklist before the order.
- Use the defect list; add photos.
- Submit promptly.

### 10.2 For supervisors
- Review before finishing.
- Send back for fixes when needed.
- Watch the dashboard for trends.
> **For Admins** — Give each role the least access it needs; keep parameter lists current; review the audit log; manage people through their company accounts; never share logins.

## 11. What's Next

### 11.1 On the horizon
- Mobile-friendly capture on the floor.
- More analytics.
- Tighter SAP automation.
- Notifications for approvals.
- Rollout to more branches.

### 11.2 How we decide
- Driven by real inspection needs.
- Improvements ship continuously.
- Your feedback shapes the next step.
