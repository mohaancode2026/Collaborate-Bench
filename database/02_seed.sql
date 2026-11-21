/* Demo data. Passwords are NOT set here: start the API with
   Seed:SetDemoPasswords=true (Development) and every user without a
   password gets Demo@123. Remove that setting in production. */
SET NOCOUNT ON;
INSERT sec.Teams(Name) VALUES (N'Team Alpha'), (N'Team Beta');

INSERT sec.Users(Email, FullName, RoleId, ManagerUserId, TeamId, MustChangePassword) VALUES
 (N'admin@collabbench.io',  N'System Admin', 1, NULL, NULL, 0);   -- 1
INSERT sec.Users(Email, FullName, RoleId, ManagerUserId, TeamId, MustChangePassword) VALUES
 (N'priya@collabbench.io',  N'Priya Sharma', 2, NULL, NULL, 0);   -- 2
DECLARE @dm INT = SCOPE_IDENTITY();
INSERT sec.Users(Email, FullName, RoleId, ManagerUserId, TeamId, MustChangePassword) VALUES
 (N'arjun@collabbench.io',  N'Arjun Reddy',  3, @dm, 1, 0);
DECLARE @tlA INT = SCOPE_IDENTITY();
INSERT sec.Users(Email, FullName, RoleId, ManagerUserId, TeamId, MustChangePassword) VALUES
 (N'kavya@collabbench.io',  N'Kavya Nair',   3, @dm, 2, 0);
DECLARE @tlB INT = SCOPE_IDENTITY();
INSERT sec.Users(Email, FullName, RoleId, ManagerUserId, TeamId, MustChangePassword) VALUES
 (N'megha@collabbench.io',  N'Megha Iyer',   4, @tlA, 1, 0),
 (N'rahul@collabbench.io',  N'Rahul Verma',  4, @tlA, 1, 0),
 (N'sanjay@collabbench.io', N'Sanjay Das',   4, @tlB, 2, 0),
 (N'neha@collabbench.io',   N'Neha Joshi',   4, @tlB, 2, 0);
INSERT sec.Users(Email, FullName, RoleId, ManagerUserId, TeamId, MustChangePassword, IsActive) VALUES
 (N'vivek@collabbench.io',  N'Vivek Rao',    4, @tlB, 2, 0, 0);
EXEC sec.usp_RebuildHierarchy;

DECLARE @megha INT=(SELECT UserId FROM sec.Users WHERE Email=N'megha@collabbench.io'),
        @rahul INT=(SELECT UserId FROM sec.Users WHERE Email=N'rahul@collabbench.io'),
        @sanjay INT=(SELECT UserId FROM sec.Users WHERE Email=N'sanjay@collabbench.io'),
        @neha INT=(SELECT UserId FROM sec.Users WHERE Email=N'neha@collabbench.io');

/* Seed runs without a session context, so temporarily switch RLS off */
ALTER SECURITY POLICY sec.TeamDataPolicy WITH (STATE = OFF);

INSERT bench.Consultants(OwnerUserId, FullName, Title, Email, Phone, VisaStatus, Location, OpenToRelocate, PayRate, ExperienceYears, Status, Skills, HotlistSummary, CreatedUtc) VALUES
 (@megha, N'Ravi Kumar',  N'Senior Java Developer',    N'ravi.kumar@mail.com',  '(555) 201-4410','H1B',   N'Dallas, TX',   1, 72, 11,'Bench',    N'["Java","Spring Boot","Microservices","AWS","Kafka","SQL"]', N'11+ yrs Java/Spring microservices on AWS for banking.', DATEADD(DAY,-6,SYSUTCDATETIME())),
 (@rahul, N'Sneha Patel', N'Java Full Stack Developer',N'sneha.patel@mail.com', '(555) 318-2290','GC',    N'Atlanta, GA',  1, 68,  9,'Bench',    N'["Java","Spring Boot","React","Kafka","PostgreSQL","Docker"]', N'9 yrs full-stack Java + React.', DATEADD(DAY,-12,SYSUTCDATETIME())),
 (@tlA,   N'James Miller',N'.NET Architect',           N'james.miller@mail.com','(555) 442-9031','USC',   N'Chicago, IL',  0, 85, 14,'Marketing',N'[".NET","C#","Azure","Microservices","SQL Server","Angular"]', N'14 yrs .NET/Azure architecture.', DATEADD(DAY,-3,SYSUTCDATETIME())),
 (@sanjay,N'Anil Gupta',  N'DevOps Engineer',          N'anil.gupta@mail.com',  '(555) 637-1188','H1B',   N'Remote',       1, 75, 10,'Marketing',N'["AWS","Kubernetes","Terraform","Jenkins","Docker","Python","Linux"]', N'10 yrs AWS/Kubernetes DevOps.', DATEADD(DAY,-9,SYSUTCDATETIME())),
 (@neha,  N'Lakshmi Rao', N'Data Engineer',            N'lakshmi.rao@mail.com', '(555) 729-6604','H4-EAD',N'Charlotte, NC',1, 70,  8,'Marketing',N'["Python","PySpark","Databricks","Snowflake","Airflow","SQL","AWS"]', N'8 yrs data engineering.', DATEADD(DAY,-15,SYSUTCDATETIME())),
 (@sanjay,N'Kevin Brooks',N'Salesforce Developer',     N'kevin.brooks@mail.com','(555) 815-3377','USC',   N'Austin, TX',   0, 78,  7,'Bench',    N'["Salesforce","Apex","LWC","JavaScript","REST"]', N'7 yrs Salesforce LWC/Apex.', DATEADD(DAY,-21,SYSUTCDATETIME())),
 (@megha, N'Fatima Khan', N'QA Automation Engineer',   N'fatima.khan@mail.com', '(555) 903-5521','GC-EAD',N'Newark, NJ',   1, 55,  8,'Marketing',N'["Selenium","Java","Cypress","Automation Testing","JIRA","Agile"]', N'8 yrs QA automation.', DATEADD(DAY,-8,SYSUTCDATETIME())),
 (@tlB,   N'Vikram Singh',N'ML Engineer',              N'vikram.singh@mail.com','(555) 274-8862','H1B',   N'San Jose, CA', 0, 90,  9,'Bench',    N'["Python","Machine Learning","PyTorch","NLP","AWS","LLM"]', N'9 yrs ML/NLP and LLM apps.', DATEADD(DAY,-4,SYSUTCDATETIME()));

INSERT bench.Requirements(Title, EndClient, Vendor, Tier, Location, WorkMode, ContractType, BillRate, Skills, AcceptedVisas, PostedByUserId, PostedUtc) VALUES
 (N'Senior Java Developer',     N'Bank of America',      N'TekSystems',    'Prime vendor',          N'Charlotte, NC',   'Hybrid','C2C', 82, N'["Java","Spring Boot","Microservices","Kafka","AWS"]', N'["H1B","GC","USC","H4-EAD"]', @tlA, DATEADD(DAY,-1,SYSUTCDATETIME())),
 (N'Azure .NET Lead',           N'Humana',               N'Infosys',       'Implementation partner',N'Louisville, KY',  'Remote','C2C', 90, N'[".NET","C#","Azure","Microservices","SQL Server"]', N'["USC","GC"]', @tlB, DATEADD(DAY,-2,SYSUTCDATETIME())),
 (N'AWS DevOps Engineer',       N'Capital One',          N'Randstad',      'Tier-1',                N'Remote',          'Remote','C2C', 85, N'["AWS","Kubernetes","Terraform","Jenkins","Python"]', N'["H1B","GC","USC"]', @sanjay, SYSUTCDATETIME()),
 (N'Snowflake Data Engineer',   N'Walgreens',            N'Cognizant',     'Implementation partner',N'Chicago, IL',     'Hybrid','W2',  78, N'["Snowflake","Python","Airflow","SQL","Databricks"]', N'["Any"]', @tlA, DATEADD(DAY,-3,SYSUTCDATETIME())),
 (N'Salesforce LWC Developer',  N'State Farm',           N'Direct client', 'Direct',                N'Bloomington, IL', 'Onsite','1099',88, N'["Salesforce","Apex","LWC"]', N'["USC","GC"]', @tlB, DATEADD(DAY,-4,SYSUTCDATETIME())),
 (N'SDET – Selenium/Java',      N'Verizon',              N'Apex Systems',  'Tier-1',                N'Irving, TX',      'Hybrid','C2C', 62, N'["Selenium","Java","Automation Testing","Cypress"]', N'["Any"]', @megha, DATEADD(DAY,-1,SYSUTCDATETIME())),
 (N'Senior React Developer',    N'Fidelity Investments', N'Infosys',       'Implementation partner',N'Boston, MA',      'Hybrid','C2C', 78, N'["React","TypeScript","Redux","Node.js","AWS"]', N'["H1B","GC","USC"]', @tlA, SYSUTCDATETIME()),
 (N'GenAI / LLM Engineer',      N'UnitedHealth Group',   N'Optum Tech',    'Prime vendor',          N'Remote',          'Remote','C2C',105, N'["Python","LLM","NLP","PyTorch","AWS"]', N'["Any"]', @dm, DATEADD(DAY,-2,SYSUTCDATETIME())),
 (N'Azure Data Engineer',       N'Johnson & Johnson',    N'TCS',           'Implementation partner',N'Remote',          'Remote','C2C', 76, N'["Azure","Databricks","PySpark","SQL","Python"]', N'["Any"]', @neha, DATEADD(DAY,-1,SYSUTCDATETIME()));

INSERT bench.Submissions(ConsultantId, RequirementId, OwnerUserId, Stage, SubmitRate, PayRate, MatchScore, RtrSigned, Notes, StageChangedUtc)
SELECT c.ConsultantId, r.RequirementId, c.OwnerUserId, v.Stage, v.SubmitRate, c.PayRate, v.Score, 1, v.Notes, DATEADD(DAY,-v.Days,SYSUTCDATETIME())
FROM (VALUES (N'Ravi Kumar',  N'Senior Java Developer','Client Interview',76,94,N'Client round 2 on Friday',1),
             (N'Anil Gupta',  N'AWS DevOps Engineer',  'Vendor Screen',  78,100,NULL,4),
             (N'James Miller',N'Azure .NET Lead',      'Offer',          88,97,N'Offer at $88/hr C2C',1),
             (N'Fatima Khan', N'SDET – Selenium/Java', 'Submitted',      57,98,NULL,5),
             (N'Lakshmi Rao', N'Azure Data Engineer',  'Submitted',      72,80,NULL,0)) v(Cons,Req,Stage,SubmitRate,Score,Notes,Days)
JOIN bench.Consultants c ON c.FullName = v.Cons
JOIN bench.Requirements r ON r.Title = v.Req;

ALTER SECURITY POLICY sec.TeamDataPolicy WITH (STATE = ON);
